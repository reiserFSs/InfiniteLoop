using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.equip;
using AscNet.Table.V2.share.character;
using AscNet.Table.V2.share.item;
using MessagePack;
using AscNet.Table.V2.share.reward;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using Newtonsoft.Json.Linq;
using System.Reflection;
using ItemUseRequest = AscNet.GameServer.Handlers.ItemUseRequest;
using ItemUseResponse = AscNet.GameServer.Handlers.ItemUseResponse;

namespace AscNet.Test;

internal static partial class Program
{
    private static void ValidateAutoUseGiftCompatibility()
    {
        Dictionary<int, ItemTable> items = TableReaderV2.Parse<ItemTable>().ToDictionary(row => row.Id);
        HashSet<int> usedMaterials = TableReaderV2.Parse<EquipBreakThroughTable>()
            .SelectMany(row => row.ItemId.Zip(row.ItemCount)
                .Where(pair => pair.First > 0 && pair.Second > 0).Select(pair => pair.First)).ToHashSet();
        int packetId = 99_850;
        long uid = 99_850;
        foreach ((int boxId, int quality) in new[] { (60001, 3), (60002, 4) })
        {
            HashSet<int> pool = usedMaterials.Where(id => items[id].Quality == quality).ToHashSet();
            AssertEqual(true, pool.Count > 1, "Auto gift has multiple positive-cost materials of its quality");
            foreach (int count in new[] { 1, 7 })
            foreach (int initialMaterials in new[] { 0, 13 })
            {
                using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
                    out RecordingMongoCollectionProxy<Player> players, out _, out RecordingMongoCollectionProxy<Inventory> inventories);
                List<Item> stock = [new Item { Id = boxId, Count = count + 2 }, new Item { Id = Inventory.Coin, Count = 123 }];
                if (initialMaterials != 0)
                    stock.AddRange(pool.Select(id => new Item { Id = id, Count = initialMaterials }));
                using LoopbackSessionHarness h = new(CreateDrawCompatibilityCharacter(++uid), CreateDrawCompatibilityPlayer(uid),
                    CreateDrawCompatibilityInventory(uid, stock), "auto-gift-success");
                h.Session.stage = CreateLoginAccountCompatibilityStage(uid);
                ItemUseRequest request = new() { Id = boxId, Count = count };
                Dictionary<int, long> before = Balances(h);
                ItemUseResponse response = Use(h, request, before, pool);
                AssertEqual(true, h.Session.player.PendingItemUse is null, "Successful use clears pending operation");
                h.Session.player = BsonSerializer.Deserialize<Player>(players.LastSuccessfulReplacementBson
                    ?? throw new InvalidDataException("Auto gift player was not durably saved."));
                h.Session.inventory = BsonSerializer.Deserialize<Inventory>(inventories.LastSuccessfulReplacementBson
                    ?? throw new InvalidDataException("Auto gift inventory was not durably saved."));
                AssertBalances(h, before, request, response);
                AssertEqual(true, h.Session.player.PendingItemUse is null, "Reload retains cleared pending operation");

                // Restoring exactly the same stock is a new acquisition, not a receipt for the old use.
                h.Session.inventory.Do(boxId, count);
                h.Session.inventory.SaveChecked();
                before = Balances(h);
                Use(h, request, before, pool);
                AssertEqual(2L * count, pool.Sum(id => Balance(h, id) - initialMaterials), "Reacquisition grants anew");
            }
        }

        // Fixed-auto gifts still resolve Reward/SubIds, not the random Drop source namespace.
        Dictionary<int, RewardGoodsTable> goodsRows = TableReaderV2.Parse<RewardGoodsTable>().ToDictionary(row => row.Id);
        Dictionary<int, RewardTable> rewardRows = TableReaderV2.Parse<RewardTable>().GroupBy(row => row.Id)
            .ToDictionary(group => group.Key, group => group.First());
        HashSet<int> characterIds = TableReaderV2.Parse<CharacterTable>().Select(row => row.Id).ToHashSet();
        ItemTable fixedAuto = items.Values.First(row => row.ItemType == (int)AscNet.Common.ItemType.Gift
            && row.SubTypeParams.Count >= 2 && row.SubTypeParams[0] == 5
            && rewardRows.TryGetValue(row.SubTypeParams[1], out RewardTable? reward)
            && reward.SubIds.Count > 0
            && reward.SubIds.All(id => goodsRows.TryGetValue(id, out RewardGoodsTable? good)
                && good.Count == 1 && characterIds.Contains(good.TemplateId)));
        List<RewardGoodsTable> fixedGoods = rewardRows[fixedAuto.SubTypeParams[1]].SubIds
            .Select(id => goodsRows[id]).ToList();
        using (MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
            out RecordingMongoCollectionProxy<Player> players, out RecordingMongoCollectionProxy<Character> characters,
            out RecordingMongoCollectionProxy<Inventory> inventories))
        using (LoopbackSessionHarness h = new(CreateDrawCompatibilityCharacter(++uid), CreateDrawCompatibilityPlayer(uid),
            CreateDrawCompatibilityInventory(uid, [new Item { Id = fixedAuto.Id, Count = 1 }]), "fixed-auto-gift"))
        {
            h.Session.stage = CreateLoginAccountCompatibilityStage(uid);
            h.Session.character.Characters.RemoveAll(character => fixedGoods.Any(good => good.TemplateId == character.Id));
            HashSet<uint> existingCharacters = h.Session.character.Characters.Select(character => character.Id).ToHashSet();
            int id = ++packetId;
            InvokeRegisteredRequestHandler(nameof(ItemUseRequest), h.Session, id, new ItemUseRequest { Id = fixedAuto.Id, Count = 1 });
            List<Packet.Push> pushes = [];
            ItemUseResponse? response = null;
            for (int index = 0; index < 16; index++)
            {
                Packet packet = h.ReadPacket("Fixed-auto gift packet");
                if (packet.Type == Packet.ContentType.Push)
                {
                    pushes.Add(MessagePackSerializer.Deserialize<Packet.Push>(packet.Content));
                    continue;
                }
                AssertEqual(Packet.ContentType.Response, packet.Type, "Fixed-auto gift terminates with response");
                Packet.Response envelope = MessagePackSerializer.Deserialize<Packet.Response>(packet.Content);
                AssertEqual(id, envelope.Id, "Fixed-auto response request identity");
                response = ReadResponsePayload<ItemUseResponse>(packet, nameof(ItemUseResponse));
                break;
            }
            AssertEqual(true, response is not null, "Fixed-auto gift returns response");
            AssertEqual(0, response!.Code, "Fixed-auto subtype 5 succeeds");
            AssertEqual(string.Join(";", fixedGoods.OrderBy(good => good.Id).Select(good => $"{good.Id}:{good.TemplateId}:{good.Count}")),
                string.Join(";", response.RewardGoodsList.OrderBy(good => good.Id).Select(good => $"{good.Id}:{good.TemplateId}:{good.Count}")),
                "Fixed-auto goods exactly match authoritative Reward subrows");
            foreach (RewardGoods good in response.RewardGoodsList)
                AssertEqual((int)RewardType.Character, good.RewardType, "Fixed-auto character reward type");
            NotifyItemDataList consumption = MessagePackSerializer.Deserialize<NotifyItemDataList>(
                pushes.Single(push => push.Name == nameof(NotifyItemDataList)).Content);
            AssertEqual(0L, consumption.ItemDataList.Single(item => item.Id == fixedAuto.Id).Count, "Fixed-auto consumes source once");
            uint[] notifiedCharacters = pushes.Where(push => push.Name == nameof(NotifyCharacterDataList))
                .SelectMany(push => MessagePackSerializer.Deserialize<NotifyCharacterDataList>(push.Content).CharacterDataList)
                .Select(character => character.Id).ToArray();
            AssertIntegerList(fixedGoods.Select(good => (long)good.TemplateId).Order().ToArray(),
                notifiedCharacters.Select(character => (long)character).Order().ToArray(), "Fixed-auto pushes actual granted characters");
            Character reloaded = BsonSerializer.Deserialize<Character>(characters.LastSuccessfulReplacementBson
                ?? throw new InvalidDataException("Fixed-auto character grant was not saved."));
            AssertIntegerList(fixedGoods.Select(good => (long)good.TemplateId).Order().ToArray(),
                reloaded.Characters.Where(character => !existingCharacters.Contains(character.Id))
                    .Select(character => (long)character.Id).Order().ToArray(), "Fixed-auto character grants survive reload");
            AssertEqual(0L, BsonSerializer.Deserialize<Inventory>(inventories.LastSuccessfulReplacementBson!)
                .Items.Single(item => item.Id == fixedAuto.Id).Count, "Fixed-auto consumption survives reload");
            AssertEqual(true, BsonSerializer.Deserialize<Player>(players.LastSuccessfulReplacementBson!).PendingItemUse is null,
                "Fixed-auto pending operation is durably cleared");
            AssertNoAvailablePacket(h, "Fixed-auto has no extra packets");
        }

        ItemTable omniframe = items[94008];
        AssertEqual(3, omniframe.SubTypeParams[0], "94008 is a choice pack");
        AssertEqual(1, omniframe.SubTypeParams[2], "94008 selects one reward");
        RewardTable omniframeReward = rewardRows[omniframe.SubTypeParams[1]];
        RewardGoodsTable firstFrame = goodsRows[omniframeReward.SubIds[0]];
        RewardGoodsTable secondFrame = goodsRows[omniframeReward.SubIds[1]];
        AssertEqual((int)RewardType.Character, (int)RewardHandler.GetRewardType(firstFrame)!, "Omniframe choice grants a frame");
        AssertEqual(true, firstFrame.TemplateId != secondFrame.TemplateId, "Omniframe choices are different frames");
        CharacterTable firstCharacter = TableReaderV2.Parse<CharacterTable>().Single(row => row.Id == firstFrame.TemplateId);
        int decompose = Character.GetMinCharacterFragment(firstCharacter.Id)?.DecomposeCount
            ?? throw new InvalidDataException("Omniframe choice has no fragment row.");
        AssertEqual(true, decompose > 0, "Duplicate omniframe converts into shards");
        ItemTable shardPick = items[40901];
        RewardGoodsTable shardChoice = goodsRows[rewardRows[shardPick.SubTypeParams[1]].SubIds[0]];
        AssertEqual((int)RewardType.Item, (int)RewardHandler.GetRewardType(shardChoice)!, "Shard pick grants an item");

        using (MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
            out RecordingMongoCollectionProxy<Player> players, out RecordingMongoCollectionProxy<Character> characters,
            out RecordingMongoCollectionProxy<Inventory> inventories))
        using (LoopbackSessionHarness h = new(CreateDrawCompatibilityCharacter(++uid), CreateDrawCompatibilityPlayer(uid),
            CreateDrawCompatibilityInventory(uid, [new Item { Id = 94008, Count = 1 }]), "choice-one-frame"))
        {
            h.Session.stage = CreateLoginAccountCompatibilityStage(uid);
            ItemUseResponse response = OpenChoice(h, new ItemUseRequest
            {
                Id = 94008, Count = 1, SelectRewardIds = [firstFrame.Id]
            });
            AssertEqual(0, response.Code, "One omniframe choice succeeds");
            AssertEqual($"{firstFrame.Id}:{firstFrame.TemplateId}:{firstFrame.Count}",
                string.Join(";", response.RewardGoodsList.Select(good => $"{good.Id}:{good.TemplateId}:{good.Count}")),
                "One omniframe choice returns the selected frame");
            AssertEqual((int)RewardType.Character, response.RewardGoodsList.Single().RewardType, "One omniframe choice reward type");
            AssertEqual(true, h.Session.character.Characters.Any(character => character.Id == (uint)firstFrame.TemplateId),
                "One omniframe choice grants the frame");
            AssertEqual(0L, Balance(h, 94008), "One omniframe choice consumes the pack");
            AssertEqual(0L, Balance(h, firstCharacter.ItemId), "A new frame is not converted into shards");
            AssertEqual(true, h.Session.player.PendingItemUse is null, "One omniframe choice clears pending");
            Character reloaded = BsonSerializer.Deserialize<Character>(characters.LastSuccessfulReplacementBson
                ?? throw new InvalidDataException("Omniframe choice was not saved."));
            AssertEqual(true, reloaded.Characters.Any(character => character.Id == (uint)firstFrame.TemplateId),
                "Omniframe choice survives reload");
            AssertEqual(0L, BsonSerializer.Deserialize<Inventory>(inventories.LastSuccessfulReplacementBson!)
                .Items.Single(item => item.Id == 94008).Count, "Omniframe choice consumption survives reload");
            AssertEqual(true, BsonSerializer.Deserialize<Player>(players.LastSuccessfulReplacementBson!).PendingItemUse is null,
                "Omniframe choice pending clear survives reload");
        }

        using (MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
            out _, out _, out _))
        using (LoopbackSessionHarness h = new(CreateDrawCompatibilityCharacter(++uid), CreateDrawCompatibilityPlayer(uid),
            CreateDrawCompatibilityInventory(uid, [new Item { Id = 94008, Count = 2 }]), "choice-repeat-frame"))
        {
            h.Session.stage = CreateLoginAccountCompatibilityStage(uid);
            ItemUseResponse response = OpenChoice(h, new ItemUseRequest
            {
                Id = 94008, Count = 2, SelectRewardIds = [firstFrame.Id]
            });
            AssertEqual(0, response.Code, "Repeated omniframe choice succeeds");
            AssertEqual($"{firstFrame.Id}:{firstFrame.TemplateId}:1;{firstFrame.Id}:{firstFrame.TemplateId}:1",
                string.Join(";", response.RewardGoodsList.Select(good => $"{good.Id}:{good.TemplateId}:{good.Count}")),
                "One selected id applies to every pack");
            AssertEqual(1, h.Session.character.Characters.Count(character => character.Id == (uint)firstFrame.TemplateId),
                "Repeated choice grants the frame once");
            AssertEqual((long)decompose, Balance(h, firstCharacter.ItemId), "The extra pack converts into that frame's shards");
            AssertEqual(0L, Balance(h, 94008), "Repeated choice consumes both packs");
        }

        using (MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
            out _, out _, out _))
        using (LoopbackSessionHarness h = new(CreateDrawCompatibilityCharacter(++uid), CreateDrawCompatibilityPlayer(uid),
            CreateDrawCompatibilityInventory(uid, [new Item { Id = 94008, Count = 2 }]), "choice-two-frames"))
        {
            h.Session.stage = CreateLoginAccountCompatibilityStage(uid);
            ItemUseResponse response = OpenChoice(h, new ItemUseRequest
            {
                Id = 94008, Count = 2, SelectRewardIds = [firstFrame.Id, secondFrame.Id]
            });
            AssertEqual(0, response.Code, "Per-pack omniframe choices succeed");
            AssertEqual($"{firstFrame.Id}:{firstFrame.TemplateId}:1;{secondFrame.Id}:{secondFrame.TemplateId}:1",
                string.Join(";", response.RewardGoodsList.Select(good => $"{good.Id}:{good.TemplateId}:{good.Count}")),
                "Per-pack choices keep their order");
            AssertEqual(true, h.Session.character.Characters.Any(character => character.Id == (uint)firstFrame.TemplateId)
                && h.Session.character.Characters.Any(character => character.Id == (uint)secondFrame.TemplateId),
                "Per-pack choices grant both frames");
            AssertEqual(0L, Balance(h, 94008), "Per-pack choices consume both packs");
        }

        using (MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
            out _, out _, out _))
        using (LoopbackSessionHarness h = new(CreateDrawCompatibilityCharacter(++uid), CreateDrawCompatibilityPlayer(uid),
            CreateDrawCompatibilityInventory(uid, [new Item { Id = 40901, Count = 3 }]), "choice-shard-pick"))
        {
            h.Session.stage = CreateLoginAccountCompatibilityStage(uid);
            ItemUseResponse response = OpenChoice(h, new ItemUseRequest
            {
                Id = 40901, Count = 3, SelectRewardIds = [shardChoice.Id]
            });
            AssertEqual(0, response.Code, "Shard pick succeeds");
            AssertEqual($"{shardChoice.Id}:{shardChoice.TemplateId}:{3 * shardChoice.Count}",
                string.Join(";", response.RewardGoodsList.Select(good => $"{good.Id}:{good.TemplateId}:{good.Count}")),
                "One shard id grants the whole stack");
            AssertEqual((int)RewardType.Item, response.RewardGoodsList.Single().RewardType, "Shard pick reward type");
            AssertEqual(1, h.Session.inventory.Items.Count(item => item.Id == shardChoice.TemplateId), "Shard pick keeps one stack");
            AssertEqual(3L * shardChoice.Count, Balance(h, shardChoice.TemplateId), "Shard pick grants one shard per pack");
            AssertEqual(0L, Balance(h, 40901), "Shard pick consumes the packs");
        }

        foreach (ItemUseRequest request in new ItemUseRequest[]
        {
            new() { Id = 94008, Count = 1 },
            new() { Id = 94008, Count = 1, SelectRewardIds = [1] },
            new() { Id = 94008, Count = 1, SelectRewardIds = [firstFrame.Id, secondFrame.Id] },
            new() { Id = 94008, Count = 2, SelectRewardIds = [firstFrame.Id, secondFrame.Id, firstFrame.Id] }
        })
        {
            using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(out _, out _, out _);
            using LoopbackSessionHarness h = new(CreateDrawCompatibilityCharacter(++uid), CreateDrawCompatibilityPlayer(uid),
                CreateDrawCompatibilityInventory(uid, [new Item { Id = 94008, Count = 4 }]), "choice-reject");
            RejectUnchanged(h, request);
        }

        using (MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
            out _, out _, out _))
        using (LoopbackSessionHarness h = new(CreateDrawCompatibilityCharacter(++uid), CreateDrawCompatibilityPlayer(uid),
            CreateDrawCompatibilityInventory(uid, [new Item { Id = 94008, Count = 2 }]), "choice-pending-selection"))
        {
            h.Session.stage = CreateLoginAccountCompatibilityStage(uid);
            h.Session.player.PendingItemUse = new ItemUsePendingOperation
            {
                ClaimKey = $"item-use:{uid}:choice",
                ItemId = 94008,
                Count = 2,
                SelectRewardIds = [firstFrame.Id],
                Goods = [PendingGood(firstFrame), PendingGood(firstFrame)]
            };
            RejectUnchanged(h, new ItemUseRequest { Id = 94008, Count = 2, SelectRewardIds = [secondFrame.Id] });
            RejectUnchanged(h, new ItemUseRequest { Id = 94008, Count = 2, SelectRewardIds = [firstFrame.Id, firstFrame.Id] });
            ItemUseResponse resumed = OpenChoice(h, new ItemUseRequest
            {
                Id = 94008, Count = 2, SelectRewardIds = [firstFrame.Id]
            });
            AssertEqual(0, resumed.Code, "Matching choice retry succeeds");
            AssertEqual(1, h.Session.character.Characters.Count(character => character.Id == (uint)firstFrame.TemplateId),
                "Matching choice retry grants the stored frame");
            AssertEqual((long)decompose, Balance(h, firstCharacter.ItemId), "Matching choice retry converts the stored extra pack");
            AssertEqual(true, h.Session.player.PendingItemUse is null, "Matching choice retry clears pending");
        }

        int unsupported = items.Values.First(row => row.ItemType == (int)AscNet.Common.ItemType.Gift
            && row.SubTypeParams.Count >= 2 && row.SubTypeParams[0] is 2 or 6
            && row.SubTypeParams[1] is not (1007 or 1008)).Id;
        foreach (ItemUseRequest request in new[]
        {
            new ItemUseRequest { Id = 60001, Count = 0 },
            new ItemUseRequest { Id = 60001, Count = -1 },
            new ItemUseRequest { Id = 60001, Count = 3 },
            new ItemUseRequest { Id = 60001, Count = 1, RecycleTime = -1 },
            new ItemUseRequest { Id = 60001, Count = 1, SelectRewardIds = [40100] },
            new ItemUseRequest { Id = unsupported, Count = 1 }
        })
        {
            using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(out var players, out var characters, out var inventories);
            using LoopbackSessionHarness h = new(CreateDrawCompatibilityCharacter(++uid), CreateDrawCompatibilityPlayer(uid),
                CreateDrawCompatibilityInventory(uid, [new Item { Id = 60001, Count = 2 }, new Item { Id = unsupported, Count = 2 }]), "auto-gift-reject");
            RejectUnchanged(h, request);
            AssertEqual(0, players.ReplaceOneCalls + characters.ReplaceOneCalls + inventories.ReplaceOneCalls, "Rejected gift never attempts persistence");
        }

        foreach (string failure in new[] { "pending", "inventory", "character", "clear", "inventory-login" })
        {
            using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
                out RecordingMongoCollectionProxy<Player> players, out RecordingMongoCollectionProxy<Character> characters,
                out RecordingMongoCollectionProxy<Inventory> inventories);
            using LoopbackSessionHarness h = new(CreateDrawCompatibilityCharacter(++uid), CreateDrawCompatibilityPlayer(uid),
                CreateDrawCompatibilityInventory(uid, [new Item { Id = 60002, Count = 5 }]), $"auto-gift-{failure}");
            h.Session.stage = CreateLoginAccountCompatibilityStage(uid);
            byte[] initialPlayer = h.Session.player.ToBson();
            byte[] initialCharacter = h.Session.character.ToBson();
            byte[] initialInventory = h.Session.inventory.ToBson();
            Dictionary<int, long> before = Balances(h);
            ItemUseRequest request = new() { Id = 60002, Count = 5 };
            bool injected = false;
            byte[]? savedPending = null;
            players.BeforeReplaceOne = document =>
            {
                if (failure == "pending" || failure == "clear" && document.PendingItemUse is null)
                {
                    injected = true;
                    throw new MongoException("Injected auto gift player persistence failure");
                }
            };
            inventories.BeforeReplaceOne = _ =>
            {
                AssertEqual(true, players.LastSuccessfulReplacementBson is not null, "Pending operation persisted before inventory grant");
                Player durable = BsonSerializer.Deserialize<Player>(players.LastSuccessfulReplacementBson!);
                AssertEqual(true, durable.PendingItemUse is not null, "Inventory grant has durable pending outcomes");
                savedPending ??= durable.PendingItemUse!.ToBson();
                if (failure is "inventory" or "inventory-login")
                {
                    injected = true;
                    throw new MongoException("Injected auto gift inventory persistence failure");
                }
            };
            characters.BeforeReplaceOne = _ =>
            {
                if (failure == "character")
                {
                    injected = true;
                    throw new MongoException("Injected auto gift character persistence failure");
                }
            };
            bool failed = false;
            try
            {
                InvokeRegisteredRequestHandler(nameof(ItemUseRequest), h.Session, ++packetId, request);
            }
            catch (InvalidDataException exception) when (exception.GetBaseException() is MongoException)
            {
                failed = true;
            }
            AssertEqual(true, failed, $"Auto gift {failure} persistence failure bubbles");
            AssertNoAvailablePacket(h, "Failed persistence emits neither response nor push");
            AssertEqual(true, injected, $"Auto gift {failure} failure reached requested persistence boundary");
            players.BeforeReplaceOne = null;
            inventories.BeforeReplaceOne = null;
            characters.BeforeReplaceOne = null;
            if (failure == "pending")
            {
                AssertEqual(Convert.ToHexString(initialPlayer), Convert.ToHexString(h.Session.player.ToBson()), "Pending save failure rolls back player");
                AssertEqual(Convert.ToHexString(initialInventory), Convert.ToHexString(h.Session.inventory.ToBson()), "Pending save failure leaves inventory untouched");
                AssertEqual(Convert.ToHexString(initialCharacter), Convert.ToHexString(h.Session.character.ToBson()), "Pending save failure leaves character untouched");
            }
            h.Session.player = BsonSerializer.Deserialize<Player>(players.LastSuccessfulReplacementBson ?? initialPlayer);
            h.Session.inventory = BsonSerializer.Deserialize<Inventory>(inventories.LastSuccessfulReplacementBson ?? initialInventory);
            h.Session.character = BsonSerializer.Deserialize<Character>(characters.LastSuccessfulReplacementBson ?? initialCharacter);
            if (failure != "pending")
            {
                ItemUsePendingOperation pending = h.Session.player.PendingItemUse
                    ?? throw new InvalidDataException("Failed gift lost its durable pending operation.");
                AssertEqual(true, !string.IsNullOrWhiteSpace(pending.ClaimKey), "Pending operation has a claim identity");
                AssertEqual(request.Id, pending.ItemId, "Pending source item");
                AssertEqual(request.Count, pending.Count, "Pending use quantity");
                AssertEqual(request.RecycleTime, pending.RecycleTime, "Pending recycle identity");
                AssertEqual(Convert.ToHexString(savedPending ?? throw new InvalidDataException("No pending outcomes captured before grant.")),
                    Convert.ToHexString(pending.ToBson()), "Save/reload preserves the exact random outcomes");
                RejectUnchanged(h, new ItemUseRequest { Id = 60001, Count = 5 });
                RejectUnchanged(h, new ItemUseRequest { Id = 60002, Count = 4 });
                RejectUnchanged(h, new ItemUseRequest { Id = 60002, Count = 5, RecycleTime = 1 });
            }
            var expectedGoods = h.Session.player.PendingItemUse?.Goods.GroupBy(good => good.TemplateId)
                .ToDictionary(group => group.Key, group => group.Sum(good => good.Count));
            if (failure == "inventory-login")
            {
                RequiredMethod(RequiredAscNetGameServerType("AscNet.GameServer.Handlers.ItemModule"), "ResumePendingItemUse",
                    BindingFlags.Public | BindingFlags.Static, [typeof(Session)]).Invoke(null, [h.Session]);
                Dictionary<int, long> expected = new(before);
                expected[request.Id] -= request.Count;
                foreach (var good in expectedGoods!)
                    expected[good.Key] = expected.GetValueOrDefault(good.Key) + good.Value;
                foreach (int itemId in expected.Keys.Union(h.Session.inventory.Items.Select(item => item.Id)))
                    AssertEqual(expected.GetValueOrDefault(itemId), Balance(h, itemId), "Login recovery grants exactly the durable outcomes");
                AssertNoAvailablePacket(h, "Pending login recovery emits no pushes");
            }
            else
            {
                ItemUseResponse resumed = Use(h, request, before, usedMaterials.Where(id => items[id].Quality == 4).ToHashSet());
                if (expectedGoods is not null)
                    AssertEqual(string.Join(";", expectedGoods.OrderBy(pair => pair.Key)),
                        string.Join(";", resumed.RewardGoodsList.GroupBy(good => good.TemplateId)
                            .ToDictionary(group => group.Key, group => group.Sum(good => good.Count)).OrderBy(pair => pair.Key)),
                        "Retry uses persisted outcomes without rerolling");
            }
            AssertEqual(true, BsonSerializer.Deserialize<Player>(players.LastSuccessfulReplacementBson!).PendingItemUse is null,
                "Retry durably clears pending operation");
            byte[] completedInventory = h.Session.inventory.ToBson();
            RequiredMethod(RequiredAscNetGameServerType("AscNet.GameServer.Handlers.ItemModule"), "ResumePendingItemUse",
                BindingFlags.Public | BindingFlags.Static, [typeof(Session)]).Invoke(null, [h.Session]);
            AssertEqual(Convert.ToHexString(completedInventory), Convert.ToHexString(h.Session.inventory.ToBson()), "Completed recovery is idempotent");
            AssertNoAvailablePacket(h, "Recovery emits no pushes");
        }

        Dictionary<int, long> Balances(LoopbackSessionHarness h) => h.Session.inventory.Items.ToDictionary(item => item.Id, item => item.Count);
        long Balance(LoopbackSessionHarness h, int id) => h.Session.inventory.Items.FirstOrDefault(item => item.Id == id)?.Count ?? 0;

        void AssertBalances(LoopbackSessionHarness h, Dictionary<int, long> before, ItemUseRequest request, ItemUseResponse response)
        {
            Dictionary<int, long> expected = new(before);
            expected[request.Id] -= request.Count;
            foreach (RewardGoods good in response.RewardGoodsList)
                expected[good.TemplateId] = expected.GetValueOrDefault(good.TemplateId) + good.Count;
            foreach (int id in expected.Keys.Union(h.Session.inventory.Items.Select(item => item.Id)))
                AssertEqual(expected.GetValueOrDefault(id), Balance(h, id), $"Gift exact balance {id}");
        }

        ItemUsePendingReward PendingGood(RewardGoodsTable row) => new()
        {
            Id = row.Id, TemplateId = row.TemplateId, Count = row.Count, Params = row.Params.ToList()
        };

        ItemUseResponse OpenChoice(LoopbackSessionHarness h, ItemUseRequest request)
        {
            int id = ++packetId;
            InvokeRegisteredRequestHandler(nameof(ItemUseRequest), h.Session, id, request);
            ItemUseResponse? response = null;
            for (int index = 0; index < 32; index++)
            {
                Packet packet = h.ReadPacket("Choice gift packet");
                if (packet.Type == Packet.ContentType.Push)
                {
                    Packet.Push push = MessagePackSerializer.Deserialize<Packet.Push>(packet.Content);
                    if (push.Name == nameof(NotifyItemDataList))
                    {
                        NotifyItemDataList notification = MessagePackSerializer.Deserialize<NotifyItemDataList>(push.Content);
                        AssertEqual(notification.ItemDataList.Count,
                            notification.ItemDataList.Select(item => item.Id).Distinct().Count(),
                            "Choice gift item notification has one row per item");
                    }
                    continue;
                }
                AssertEqual(Packet.ContentType.Response, packet.Type, "Choice gift terminates with response");
                Packet.Response envelope = MessagePackSerializer.Deserialize<Packet.Response>(packet.Content);
                AssertEqual(id, envelope.Id, "Choice gift response identity");
                response = ReadResponsePayload<ItemUseResponse>(packet, nameof(ItemUseResponse));
                break;
            }
            AssertEqual(true, response is not null, "Choice gift returns a response");
            AssertNoAvailablePacket(h, "Choice gift has no extra packets");
            return response!;
        }

        ItemUseResponse Use(LoopbackSessionHarness h, ItemUseRequest request, Dictionary<int, long> before, HashSet<int> pool)
        {
            int id = ++packetId;
            InvokeRegisteredRequestHandler(nameof(ItemUseRequest), h.Session, id, request);
            Packet first = h.ReadPacket("Auto gift first packet");
            if (first.Type == Packet.ContentType.Response)
                AssertEqual(0, ReadResponsePayload<ItemUseResponse>(first, nameof(ItemUseResponse)).Code,
                    "Auto gift subtype 6 must succeed before expecting its notification");
            AssertEqual(Packet.ContentType.Push, first.Type, "Auto gift success starts with combined notification");
            Packet.Push push = MessagePackSerializer.Deserialize<Packet.Push>(first.Content);
            AssertEqual(nameof(NotifyItemDataList), push.Name, "Auto gift notification name");
            JObject wire = JObject.Parse(MessagePackSerializer.ConvertToJson(push.Content));
            AssertEqual("ItemDataList,ItemRecycleDict", string.Join(",", wire.Properties().Select(property => property.Name).Order()), "Combined item notification schema");
            NotifyItemDataList notification = MessagePackSerializer.Deserialize<NotifyItemDataList>(push.Content);
            ItemUseResponse response = ReadResponsePayload<ItemUseResponse>(h, id, nameof(ItemUseResponse), "Auto gift response after single notification");
            AssertEqual(0, response.Code, "Auto gift succeeds");
            AssertEqual(request.Count, response.RewardGoodsList.Sum(good => good.Count), "Each box grants exactly one material");
            foreach (RewardGoods good in response.RewardGoodsList)
            {
                AssertEqual((int)RewardType.Item, good.RewardType, "Auto gift grants item goods");
                AssertEqual(true, pool.Contains(good.TemplateId), "Auto gift material belongs to positive-cost quality pool");
                AssertEqual(true, good.Count > 0, "Auto gift material quantity is positive");
            }
            AssertBalances(h, before, request, response);
            int[] changedIds = response.RewardGoodsList.Select(good => good.TemplateId).Append(request.Id).Distinct().Order().ToArray();
            AssertIntegerList(changedIds.Select(value => (long)value).ToArray(),
                notification.ItemDataList.Select(item => (long)item.Id).Order().ToArray(), "Combined notification contains exactly source and actual rewards without duplicates");
            foreach (Item item in notification.ItemDataList)
                AssertEqual(Balance(h, item.Id), item.Count, "Combined notification uses final inventory balances");
            AssertNoAvailablePacket(h, "Auto gift has no extra reward or consume push");
            return response;
        }

        void Reject(LoopbackSessionHarness h, ItemUseRequest request)
        {
            int id = ++packetId;
            InvokeRegisteredRequestHandler(nameof(ItemUseRequest), h.Session, id, request);
            ItemUseResponse response = ReadResponsePayload<ItemUseResponse>(h, id, nameof(ItemUseResponse), "Auto gift rejected without push");
            AssertEqual(true, response.Code != 0, "Auto gift rejection code");
            AssertEqual(0, response.RewardGoodsList.Count, "Rejected gift exposes no granted goods");
            AssertNoAvailablePacket(h, "Rejected gift emits no packets beyond response");
        }

        void RejectUnchanged(LoopbackSessionHarness h, ItemUseRequest request)
        {
            byte[] player = h.Session.player.ToBson();
            byte[] inventory = h.Session.inventory.ToBson();
            byte[] character = h.Session.character.ToBson();
            Reject(h, request);
            AssertEqual(Convert.ToHexString(player), Convert.ToHexString(h.Session.player.ToBson()), "Rejected gift leaves player unchanged");
            AssertEqual(Convert.ToHexString(inventory), Convert.ToHexString(h.Session.inventory.ToBson()), "Rejected gift leaves inventory unchanged");
            AssertEqual(Convert.ToHexString(character), Convert.ToHexString(h.Session.character.ToBson()), "Rejected gift leaves character unchanged");
        }
    }
}
