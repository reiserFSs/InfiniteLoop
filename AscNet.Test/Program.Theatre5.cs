using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.Table.V2.share.theatre5;
using MessagePack;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using Newtonsoft.Json.Linq;
using System.Reflection;
using Character = AscNet.Common.Database.Character;
using Inventory = AscNet.Common.Database.Inventory;

namespace AscNet.Test;

internal partial class Program
{
    private static readonly HashSet<string> GodfallCalls = [];
    private static readonly HashSet<string> GodfallSuccessfulCalls = [];
    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidDataException(message);
    }

    private static void ValidateTheatre5Compatibility()
    {
        PacketFactory.LoadPacketHandlers();
        GodfallCalls.Clear();
        GodfallSuccessfulCalls.Clear();
        ValidateGodfallLifecycleChecks();
        ValidateGodfallSkillChoiceShape();
        ValidateGodfallLoginPreparationChecks();
        ValidateGodfallJournalChecks();
        ValidateGodfallEconomyChecks();
        ValidateGodfallMetaChecks();
        ValidateGodfallStoryChecks();
        RunGodfallBattleChecks();
        string[] requests = [
            nameof(XTheatre5SkillChoiceRequest), nameof(XTheatre5ShopUnlockGridRequest), nameof(Theatre5EnterShopRequest),
            nameof(XTheatre5ShopBuyItemRequest), nameof(XTheatre5ShopSellItemRequest), nameof(XTheatre5ShopRefreshRequest),
            nameof(XTheatre5BagItemMoveRequest), nameof(XTheatre5ShopFreezeRequest), nameof(PveOrPvpChangeRequest),
            nameof(DlcSingleEnterFightRequest), nameof(Theatre5CharacterSkinSetRequest), nameof(Theatre5MissionFreshRequest),
            nameof(Theatre5MissionChooseRequest), nameof(Theatre5MissionLevelUpRequest), nameof(Theatre5MissionRewardRequest),
            nameof(XTheatre5CharacterLevelUpRequest), nameof(XTheatre5RelicRefreshRequest), nameof(XTheatre5RelicChooseRequest),
            nameof(XTheatre5HammerStrengthenRequest), nameof(XTheatre5BuyExpRequest), nameof(PveStoryLinePromoteRequest),
            nameof(PveEventPromoteRequest), nameof(PveChapterEnterRequest), nameof(XTheatre5ItemBoxSelectRequest),
            nameof(XTheatre5ItemBoxOpenRequest), nameof(PveAvgPlayRequest), nameof(XTheatre5PveAnswerQuestionRequest),
            nameof(Theatre5InitGameRequest), nameof(XTheatre5QueryRankRequest), nameof(DlcSingleFightSettleRequest),
            nameof(Theatre5SettleExtraChoiceRequest), nameof(Theatre5MatchRequest)
        ];
        Require(requests.All(GodfallCalls.Contains), "Godfall requests not exercised: " + string.Join(", ", requests.Except(GodfallCalls)));
        Require(requests.All(GodfallSuccessfulCalls.Contains), "Godfall requests without successful flow: " + string.Join(", ", requests.Except(GodfallSuccessfulCalls)));
        Console.WriteLine("Theatre5 registered-packet/BSON compatibility passed; synthetic native reports prove server boundaries, not StatusSyncFight gameplay. Missing reward-shop catalogs are rejection-only coverage.");
    }

    private static void ValidateGodfallLifecycleChecks()
    {
        using var test = new GodfallCase("lifecycle");
        test.Start();
        byte[] run = test.Adventure.ToBson();
        int gold = test.Adventure.GoldNum;
        int visits = test.Adventure.EnterShopCnt;
        test.Call(nameof(Theatre5EnterShopRequest));
        AssertEqual(gold, test.Adventure.GoldNum, "shop reentry cannot mint round income");
        AssertEqual(visits, test.Adventure.EnterShopCnt, "shop reentry cannot trigger another visit");
        test.Call(nameof(PveOrPvpChangeRequest));
        AssertEqual(true, run.SequenceEqual(test.Data.PvpAdventureData!.ToBson<Theatre5AdventureData>()), "mode switch preserves inactive PvP adventure");
        test.Call(nameof(PveOrPvpChangeRequest));
        test.Relog("shopping");
        AssertEqual(true, run.SequenceEqual(test.Adventure.ToBson()), "BSON relog preserves shop, inventory, and random choices");
        var character = TableReaderV2.Parse<Theatre5CharacterTable>().Single(row => row.Id == test.Adventure.CharacterId);
        test.Call(nameof(Theatre5CharacterSkinSetRequest), new Theatre5CharacterSkinSetRequest
        { CharacterId = character.Id, FashionId = character.FashionIds[0] });
        test.Reject(nameof(Theatre5CharacterSkinSetRequest), new Theatre5CharacterSkinSetRequest
        { CharacterId = character.Id, FashionId = int.MaxValue }, "foreign fashion");
        var freeCoating = TableReaderV2.Parse<Theatre5CharacterFashionTable>().First(row =>
            character.FashionIds.Contains(row.Id) && row.MainlineFashionId > 0
            && !test.Session.character.Fashions.Any(owned => owned.Id == row.MainlineFashionId && !owned.IsLock));
        byte[] accountCoatings = test.Session.character.ToBson();
        test.Call(nameof(Theatre5CharacterSkinSetRequest), new Theatre5CharacterSkinSetRequest
        { CharacterId = character.Id, FashionId = freeCoating.Id });
        AssertEqual(freeCoating.Id, test.Data.Characters[character.Id].FashionId, "Authored free mode coating does not require account ownership");
        AssertEqual(true, accountCoatings.SequenceEqual(test.Session.character.ToBson()), "Mode coating selection grants no account coating");
        test.Relog("free-mode-coating");
        AssertEqual(freeCoating.Id, test.Data.Characters[character.Id].FashionId, "Free mode coating survives BSON relog");
        var rank = test.Call(nameof(XTheatre5QueryRankRequest), new XTheatre5QueryRankRequest { CharacterId = 0 });
        Require(rank["RankPlayerInfos"] is JArray, "rank uses ordered array");
        AssertEqual(0, rank.Value<int>("TotalCount"), "empty persisted board never fabricates robot rankings");
        var rival = BsonSerializer.Deserialize<Player>(test.Player.ToBson());
        rival.PlayerData.Id = test.Player.PlayerData.Id + 1;
        rival.PlayerData.Name = "Godfall board fixture";
        rival.Theatre5.Data.Characters[character.Id].Rating = test.Data.Characters[character.Id].Rating + 100;
        test.Players.FindResults = [BsonSerializer.Deserialize<Player>(test.Player.ToBson()), rival];
        rank = test.Call(nameof(XTheatre5QueryRankRequest), new XTheatre5QueryRankRequest { CharacterId = character.Id });
        AssertEqual(2, rank.Value<int>("TotalCount"), "ranking counts persisted eligible players");
        AssertEqual(2, rank.Value<int>("SelfRank"), "self rank follows persisted rating order");
        AssertEqual(rival.PlayerData.Id, rank["RankPlayerInfos"]![0]!.Value<long>("Id"), "higher persisted rating ranks first");
        AssertEqual(rival.Theatre5.Data.Characters[character.Id].Rating, rank["RankPlayerInfos"]![0]!.Value<int>("Score"), "ranking displays persisted score");
    }

    private static void ValidateGodfallSkillChoiceShape()
    {
        var item = new Theatre5Item { InstanceId = 73, ItemId = 10101, ItemType = 1 };
        var activeRun = new Theatre5PveAdventureData
        {
            Status = 3,
            RoundNum = 4,
            GoldNum = 19,
            SkillChoiceData = new() { SkillGroups = [item] },
            PveChapterData = new() { ChapterId = 34 }
        };
        BsonDocument legacy = activeRun.ToBsonDocument();
        legacy[nameof(Theatre5AdventureData.SkillChoiceData)].AsBsonDocument[nameof(Theatre5SkillChoiceData.SkillGroups)] =
            new BsonArray { new BsonDocument(nameof(Theatre5Goods.ItemInfo), item.ToBsonDocument()) };

        Theatre5PveAdventureData recovered = BsonSerializer.Deserialize<Theatre5PveAdventureData>(legacy);
        AssertEqual(3, recovered.Status, "Legacy active skill interruption remains active");
        AssertEqual(4, recovered.RoundNum, "Legacy active run keeps tutorial progress");
        AssertEqual(19, recovered.GoldNum, "Legacy active run keeps inventory currency");
        AssertEqual(34, recovered.PveChapterData!.ChapterId, "Legacy active run keeps chapter progress");
        AssertEqual(item.InstanceId, recovered.SkillChoiceData!.SkillGroups.Single().InstanceId,
            "Legacy wrapped skill choice recovers the exact selectable instance");
        BsonDocument migrated = recovered.ToBsonDocument();
        BsonDocument storedChoice = migrated[nameof(Theatre5AdventureData.SkillChoiceData)].AsBsonDocument
            [nameof(Theatre5SkillChoiceData.SkillGroups)].AsBsonArray.Single().AsBsonDocument;
        AssertEqual(false, storedChoice.Contains(nameof(Theatre5Goods.ItemInfo)),
            "Recovered active run is rewritten once as flat skill storage");
        AssertEqual(item.ItemId, storedChoice[nameof(Theatre5Item.ItemId)].AsInt32,
            "Migrated skill storage retains the exact item");
        var journal = new PlayerTheatre5State
        {
            PendingMutation = new()
            {
                RequestKey = "Theatre5EnterShopRequest:{}",
                Outcome = new() { Data = new() { PvpType = 2, PveAdventureData = activeRun } },
                Grants = [new() { ClaimKey = "keep", Goods = [new() { Id = 1, Count = 2 }] }],
                Pushes = [new() { Name = "keep", Payload = [1, 2, 3] }]
            }
        };
        BsonDocument legacyJournal = journal.ToBsonDocument();
        BsonDocument journalChoice = legacyJournal[nameof(PlayerTheatre5State.PendingMutation)].AsBsonDocument
            [nameof(Theatre5PendingMutation.Outcome)].AsBsonDocument[nameof(PlayerTheatre5State.Data)].AsBsonDocument
            [nameof(Theatre5DataDb.PveAdventureData)].AsBsonDocument[nameof(Theatre5AdventureData.SkillChoiceData)].AsBsonDocument;
        journalChoice[nameof(Theatre5SkillChoiceData.SkillGroups)] =
            new BsonArray { new BsonDocument(nameof(Theatre5Goods.ItemInfo), item.ToBsonDocument()) };
        PlayerTheatre5State recoveredJournal = BsonSerializer.Deserialize<PlayerTheatre5State>(legacyJournal);
        AssertEqual(item.InstanceId, recoveredJournal.PendingMutation!.Outcome.Data.PveAdventureData!
            .SkillChoiceData!.SkillGroups.Single().InstanceId, "Pending outcome recovers its exact skill choice");
        AssertEqual("keep", recoveredJournal.PendingMutation.Grants.Single().ClaimKey,
            "Skill migration preserves pending reward journal");
        AssertEqual(3, recoveredJournal.PendingMutation.Pushes.Single().Payload.Length,
            "Skill migration preserves frozen pending push bytes");

        JObject wire = JObject.Parse(MessagePackSerializer.ConvertToJson(
            MessagePackSerializer.Serialize(new Theatre5SkillChoiceData { SkillGroups = [item] })));
        JToken visible = wire[nameof(Theatre5SkillChoiceData.SkillGroups)]!.Single();
        AssertEqual(item.InstanceId, visible.Value<int>(nameof(Theatre5Item.InstanceId)), "UI skill card receives InstanceId directly");
        AssertEqual(item.ItemId, visible.Value<int>(nameof(Theatre5Item.ItemId)), "UI skill card receives ItemId directly");
        AssertEqual(item.ItemType, visible.Value<int>(nameof(Theatre5Item.ItemType)), "UI skill card receives ItemType directly");
        AssertEqual<JToken?>(null, visible[nameof(Theatre5Goods.ItemInfo)], "UI skill card is not wrapped as shop goods");
        JObject shopWire = JObject.Parse(MessagePackSerializer.ConvertToJson(MessagePackSerializer.Serialize(
            new Theatre5ShopData { Goods = [new() { ItemInfo = item }] })));
        AssertEqual(item.ItemId, shopWire[nameof(Theatre5ShopData.Goods)]!.Single()!
            [nameof(Theatre5Goods.ItemInfo)]!.Value<int>(nameof(Theatre5Item.ItemId)),
            "Normal shop goods remain wrapped with pricing state");
    }

    private static void ValidateGodfallLoginPreparationChecks()
    {
        foreach (bool inactive in new[] { false, true })
        {
            using var test = new GodfallCase(inactive ? "inactive-init-disconnect" : "init-disconnect");
            test.Call(nameof(PveOrPvpChangeRequest));
            test.Call(nameof(Theatre5InitGameRequest), new Theatre5InitGameRequest { CharacterId = test.Data.Characters.Keys.Min() });
            AssertEqual(2, test.Adventure.Status, "Init response preserves normal pre-shop client sequence");
            if (inactive) test.Call(nameof(PveOrPvpChangeRequest));
            int selectedMode = test.Data.PvpType;
            test.Relog("disconnect-before-enter-shop", pending: true);
            AssertEqual(selectedMode, test.Data.PvpType, "Login preparation preserves selected mode");
            var adventure = test.Data.PvpAdventureData!;
            Require(adventure.Status is 3 or 4 && adventure.ShopData != null, "Init-only disconnect resumes actionable shop");
            AssertEqual(1, adventure.EnterShopCnt, "Login prepares first shop exactly once");
            byte[] prepared = adventure.ToBson();
            test.Relog("prepared-shop-repeat");
            AssertEqual(true, prepared.SequenceEqual(test.Data.PvpAdventureData!.ToBson()), "Repeated login cannot reroll offers or repay income");
            if (inactive) test.Call(nameof(PveOrPvpChangeRequest));
            test.Call(nameof(Theatre5EnterShopRequest));
            AssertEqual(true, prepared.SequenceEqual(test.Data.PvpAdventureData!.ToBson()), "Client EnterShop after recovery reuses offers and income");
        }
    }

    private static void ValidateGodfallJournalChecks()
    {
        foreach (bool finalCommit in new[] { false, true })
        {
            using var test = new GodfallCase(finalCommit ? "final-commit" : "intent-save");
            test.Start();
            byte[] before = test.Adventure.ToBson();
            var request = new XTheatre5ShopFreezeRequest { InstanceId = -1, IsFreeze = true };
            int saves = 0;
            test.Players.BeforeReplaceOne = _ =>
            {
                if (++saves == (finalCommit ? 2 : 1)) throw new MongoException("Godfall injected journal boundary");
            };
            try { test.Call(nameof(XTheatre5ShopFreezeRequest), request, success: false); }
            finally { test.Players.BeforeReplaceOne = null; }
            AssertEqual(true, before.SequenceEqual(test.Adventure.ToBson()), "failed journal boundary preserves visible adventure");
            AssertEqual(finalCommit, test.State.PendingMutation != null, "only durable intent remains recoverable");
            AssertEqual(0, test.Pushes.Count, "failed journal does not acknowledge mutation pushes");
            if (finalCommit)
            {
                byte[] pending = test.State.ToBson();
                test.Call(nameof(PveOrPvpChangeRequest), success: false);
                AssertEqual(true, pending.SequenceEqual(test.State.ToBson()), "unrelated mode switch cannot consume pending owner");
                test.Call(nameof(XTheatre5ShopFreezeRequest), new XTheatre5ShopFreezeRequest { InstanceId = -1, IsFreeze = false }, success: false);
                AssertEqual(true, pending.SequenceEqual(test.State.ToBson()), "different semantic request cannot recover pending outcome");
            }
            test.Call(nameof(XTheatre5ShopFreezeRequest), request);
            Require(test.Adventure.ShopData!.Goods.All(goods => goods.IsFreeze), "semantic retry with new ID applies frozen intent");
            Require(test.State.PendingMutation == null, "recovery clears pending journal");
            byte[] committed = test.State.ToBson();
            byte[] response = test.LastResponseContent;
            int committedId = test.LastPacketId;
            test.Call(nameof(XTheatre5ShopFreezeRequest), request, reusePacketId: committedId);
            AssertEqual(true, response.SequenceEqual(test.LastResponseContent), "committed retry returns frozen response bytes");
            AssertEqual(0, test.Pushes.Count, "committed duplicate is response only");
            AssertEqual(true, committed.SequenceEqual(test.State.ToBson()), "committed duplicate cannot mutate state");
            test.Relog("journal");
            Require(test.Adventure.ShopData!.Goods.All(goods => goods.IsFreeze), "frozen shop survives BSON reload");
        }
        using (var test = new GodfallCase("receipt-mode-owner"))
        {
            test.Start();
            var freeze = new XTheatre5ShopFreezeRequest { InstanceId = -1, IsFreeze = true };
            test.Call(nameof(XTheatre5ShopFreezeRequest), freeze);
            int shopPacket = test.LastPacketId;
            test.Call(nameof(PveOrPvpChangeRequest));
            int togglePacket = test.LastPacketId;
            byte[] toggleResponse = test.LastResponseContent;
            byte[] switched = test.State.ToBson();
            test.Call(nameof(XTheatre5ShopFreezeRequest), freeze, success: false, reusePacketId: shopPacket);
            AssertEqual(true, switched.SequenceEqual(test.State.ToBson()), "Old-mode shop receipt cannot replay into selected PvE");
            AssertEqual(0, test.Pushes.Count, "Old-mode receipt rejection cannot hydrate inactive shop");
            test.Call(nameof(PveOrPvpChangeRequest), reusePacketId: togglePacket);
            AssertEqual(2, test.Data.PvpType, "Committed toggle duplicate cannot toggle back");
            AssertEqual(true, toggleResponse.SequenceEqual(test.LastResponseContent), "Toggle receipt belongs to resulting mode");
            AssertEqual(true, switched.SequenceEqual(test.State.ToBson()), "Toggle duplicate preserves both adventures");
            AssertEqual(0, test.Pushes.Count, "Committed toggle duplicate is response-only");
        }
        using (var test = new GodfallCase("pending-mode-toggle"))
        {
            test.Start();
            int saves = 0;
            test.Players.BeforeReplaceOne = _ =>
            {
                if (++saves == 2) throw new MongoException("Godfall injected toggle final commit failure");
            };
            try { test.Call(nameof(PveOrPvpChangeRequest), success: false); }
            finally { test.Players.BeforeReplaceOne = null; }
            AssertEqual(1, test.Data.PvpType, "Failed toggle leaves prior live mode selected");
            Require(test.State.PendingMutation != null, "Failed toggle retains durable outcome");
            test.ReloadDurable();
            test.Call(nameof(PveOrPvpChangeRequest));
            AssertEqual(2, test.Data.PvpType, "New-ID semantic toggle retry restores outcome rather than toggling it twice");
            Require(test.State.PendingMutation == null, "Recovered toggle clears its journal");
            byte[] recovered = test.State.ToBson();
            test.Call(nameof(PveOrPvpChangeRequest), reusePacketId: test.LastPacketId);
            AssertEqual(true, recovered.SequenceEqual(test.State.ToBson()), "Recovered toggle receipt is valid in resulting mode");
            AssertEqual(0, test.Pushes.Count, "Recovered committed toggle duplicate is response-only");
        }
    }

    private sealed class GodfallCase : IDisposable
    {
        private static long nextPlayerId = 190_000;
        private int packetId;
        private readonly MongoCollectionOverride collections;
        public readonly RecordingMongoCollectionProxy<Player> Players;
        public readonly RecordingMongoCollectionProxy<Character> Characters;
        public readonly RecordingMongoCollectionProxy<Inventory> Inventories;
        public readonly RecordingMongoCollectionProxy<Stage> Stages;
        public LoopbackSessionHarness Harness { get; private set; }
        public Session Session => Harness.Session;
        public Player Player => Session.player;
        public PlayerTheatre5State State => Player.Theatre5;
        public Theatre5DataDb Data => State.Data;
        public Theatre5AdventureData Adventure => (Data.PvpType == 1 ? Data.PvpAdventureData : (Theatre5AdventureData?)Data.PveAdventureData)
            ?? throw new InvalidDataException("Godfall has no current adventure.");
        public List<Packet.Push> Pushes { get; } = [];
        public byte[] LastResponseContent { get; private set; } = [];
        public int LastPacketId { get; private set; }
        public string LastRequestName { get; private set; } = "";
        public object? LastRequest { get; private set; }

        public GodfallCase(string name)
        {
            collections = MongoCollectionOverride.InstallForBiancaCompatibility(out Players, out Characters, out Inventories, out Stages);
            Players.FindResults = [];
            long id = ++nextPlayerId;
            Harness = new(CreateDrawCompatibilityCharacter(id), CreateDrawCompatibilityPlayer(id),
                CreateDrawCompatibilityInventory(id, []), $"theatre5-{name}");
            Session.stage = CreateLoginAccountCompatibilityStage(id);
            Player.PlayerData.Level = 70;
            PrepareLogin();
            _ = BuildTaskData(Session);
            AscNet.GameServer.Handlers.ArchiveCgModule.Reconcile(Session, notify: false);
            SaveFixture();
        }
        private void PrepareLogin() => RequiredMethod(RequiredAscNetGameServerType("AscNet.GameServer.Handlers.Theatre5Module"),
            "PrepareLogin", BindingFlags.Static | BindingFlags.NonPublic, [typeof(Session)]).Invoke(null, [Session]);
        public Theatre5DataDb Login() => ((NotifyTheatre5ActivityData)RequiredMethod(
            RequiredAscNetGameServerType("AscNet.GameServer.Handlers.Theatre5Module"), "BuildLoginData",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public, [typeof(Session)]).Invoke(null, [Session])!).Theatre5DataDb;
        public void SaveFixture()
        {
            Player.SaveChecked();
            Session.character.SaveChecked();
            Session.inventory.SaveChecked();
        }
        public void Save() => SaveFixture();
        public void Start()
        {
            if (Data.PvpType != 1) Call(nameof(PveOrPvpChangeRequest));
            Call(nameof(Theatre5InitGameRequest), new Theatre5InitGameRequest { CharacterId = Data.Characters.Keys.Min() });
            Call(nameof(Theatre5EnterShopRequest));
            if (Adventure.Status == 3)
                Call(nameof(XTheatre5SkillChoiceRequest), new XTheatre5SkillChoiceRequest
                { InstanceId = Adventure.SkillChoiceData!.SkillGroups[0].InstanceId, IsEquipped = true, TargetIndex = 1 });
        }
        public JObject Success(string name, object? request = null) => Call(name, request);
        public JObject Call(string requestName, object? request = null, bool? success = true, int? reusePacketId = null)
        {
            Pushes.Clear();
            int id = reusePacketId ?? ++packetId;
            LastPacketId = id;
            int fenceId = 0;
            JObject? body = null;
            LastRequestName = requestName;
            LastRequest = request;
            GodfallCalls.Add(requestName);
            Harness.WriteClientBytes(LoopbackSessionHarness.SerializeClientRequestFrame(requestName, id, request));
            for (int index = 0; index < 256; index++)
            {
                Packet packet = Harness.ReadPacket($"{requestName} result {index}");
                if (packet.Type == Packet.ContentType.Push)
                {
                    Pushes.Add(MessagePackSerializer.Deserialize<Packet.Push>(packet.Content));
                    continue;
                }
                AssertEqual(Packet.ContentType.Response, packet.Type, $"{requestName} packet type");
                var response = MessagePackSerializer.Deserialize<Packet.Response>(packet.Content);
                if (fenceId != 0)
                {
                    AssertEqual(fenceId, response.Id, $"{requestName} completion fence correlation");
                    AssertEqual(nameof(HandshakeResponse), response.Name, $"{requestName} completion fence response");
                    AssertEqual(0, MessagePackSerializer.Deserialize<HandshakeResponse>(response.Content).Code, $"{requestName} completion fence success");
                    return body ?? throw new InvalidDataException($"{requestName}: fence preceded tested response");
                }
                AssertEqual(id, response.Id, $"{requestName} correlation");
                AssertEqual(requestName.Replace("Request", "Response", StringComparison.Ordinal), response.Name, $"{requestName} response");
                LastResponseContent = response.Content;
                body = JObject.Parse(MessagePackSerializer.ConvertToJson(response.Content));
                int code = RequiredValue<int>(body, "Code", JTokenType.Integer, requestName);
                if (code == 0) GodfallSuccessfulCalls.Add(requestName);
                if (success.HasValue) AssertEqual(success.Value, code == 0, $"{requestName} success={success}, code={code}");
                // Inbound dispatch is serial: this response fences the tested request's task/archive post-hooks.
                // It is transport-only, uses the same counter, and never replaces LastPacketId or coverage.
                fenceId = ++packetId;
                Harness.WriteClientBytes(LoopbackSessionHarness.SerializeClientRequestFrame(nameof(HandshakeRequest), fenceId,
                    new HandshakeRequest { DocumentVersion = "", Sha1 = "", ApplicationVersion = "" }));
            }
            throw new InvalidDataException($"{requestName}: response missing");
        }
        public void Error(string name, object? request, int code) => AssertEqual(code, Call(name, request, false).Value<int>("Code"), name);
        public void Reject(string name, object? request, string label)
        {
            byte[] data = Data.ToBson();
            byte[] inventory = Session.inventory.ToBson();
            byte[] character = Session.character.ToBson();
            Call(name, request, false);
            AssertEqual(true, data.SequenceEqual(Data.ToBson()), label + ": adventure unchanged");
            AssertEqual(true, inventory.SequenceEqual(Session.inventory.ToBson()), label + ": inventory unchanged");
            AssertEqual(true, character.SequenceEqual(Session.character.ToBson()), label + ": character unchanged");
        }
        public void Relog(string name, bool pending = false, bool prepareLogin = true)
        {
            byte[] before = Data.ToBson();
            var player = BsonSerializer.Deserialize<Player>(Players.LastSuccessfulReplacementBson!);
            var character = BsonSerializer.Deserialize<Character>(Characters.LastSuccessfulReplacementBson ?? Session.character.ToBson());
            var inventory = BsonSerializer.Deserialize<Inventory>(Inventories.LastSuccessfulReplacementBson ?? Session.inventory.ToBson());
            var stage = BsonSerializer.Deserialize<Stage>(Session.stage.ToBson());
            Harness.Dispose();
            Harness = new(character, player, inventory, $"theatre5-relog-{name}");
            Session.stage = stage;
            if (prepareLogin) PrepareLogin();
            if (!pending && !before.SequenceEqual(Login().ToBson()))
            {
                var expected = BsonSerializer.Deserialize<BsonDocument>(before);
                var actual = Login().ToBsonDocument();
                string differences = string.Join("; ", expected.Names.Concat(actual.Names).Distinct()
                    .Where(key => !expected.GetValue(key, BsonNull.Value).Equals(actual.GetValue(key, BsonNull.Value)))
                    .Select(key => $"{key}: {expected.GetValue(key, BsonNull.Value)} -> {actual.GetValue(key, BsonNull.Value)}"));
                throw new InvalidDataException($"{name}: login changed durable mode state: {differences}");
            }
        }
        public void Reload() => Relog("reload");
        public void ReloadDurable() => Relog("durable-retry", pending: true, prepareLogin: false);
        public long Balance(int itemId) => Session.inventory.Items.SingleOrDefault(item => item.Id == itemId)?.Count ?? 0;
        public void Dispose() { Harness.Dispose(); collections.Dispose(); }
    }
}
