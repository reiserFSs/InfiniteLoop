using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Handlers;
using AscNet.GameServer.Handlers.Drops;
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

        int[] coatingChoices = [400076, 400078, 400079, 400080, 400081, 400082, 40913];
        int[] shardChoices = [40905, 40906, 40907, 40908, 40914, 40915, 40916, 40918, 40919, 40920, 40921, 40922, 40923, 40924, 40925];
        foreach (int choiceId in coatingChoices.Concat(shardChoices))
        {
            ItemTable choice = items[choiceId];
            AssertEqual(3, choice.SubTypeParams[0], $"{choiceId} is a choice pack");
            AssertEqual(1, choice.SubTypeParams[2], $"{choiceId} selects one reward");
            RewardTable choiceReward = rewardRows[choice.SubTypeParams[1]];
            AssertEqual(true, choiceReward.SubIds.Count > 0 && choiceReward.SubIds.All(goodsRows.ContainsKey),
                $"{choiceId} reward goods exist");
            foreach (int subId in choiceReward.SubIds)
            {
                RewardGoodsTable option = goodsRows[subId];
                ItemTable granted = items[option.TemplateId];
                if (coatingChoices.Contains(choiceId))
                {
                    AssertEqual((int)AscNet.Common.ItemType.WeaponFashion, granted.ItemType,
                        $"{choiceId} option {subId} is a weapon coating");
                    AssertEqual(true, granted.SubTypeParams.Count > 0 && granted.SubTypeParams[0] > 0,
                        $"{choiceId} coating maps to a fashion id");
                }
                else
                {
                    AssertEqual((int)AscNet.Common.ItemType.Fragment, granted.ItemType,
                        $"{choiceId} option {subId} is an inver-shard");
                }
            }
        }

        RewardGoodsTable firstCoating = goodsRows[rewardRows[items[400076].SubTypeParams[1]].SubIds[0]];
        RewardGoodsTable secondCoating = goodsRows[rewardRows[items[400076].SubTypeParams[1]].SubIds[1]];
        RewardGoodsTable firstShard = goodsRows[rewardRows[items[40905].SubTypeParams[1]].SubIds[0]];
        int firstFashion = items[firstCoating.TemplateId].SubTypeParams[0];
        int secondFashion = items[secondCoating.TemplateId].SubTypeParams[0];
        using (MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
            out _, out _, out _))
        using (LoopbackSessionHarness h = new(CreateDrawCompatibilityCharacter(++uid), CreateDrawCompatibilityPlayer(uid),
            CreateDrawCompatibilityInventory(uid, [new Item { Id = 400076, Count = 1 }]), "coating-one"))
        {
            h.Session.stage = CreateLoginAccountCompatibilityStage(uid);
            ItemUseResponse response = OpenChoice(h, new ItemUseRequest
            {
                Id = 400076, Count = 1, SelectRewardIds = [firstCoating.Id]
            });
            AssertEqual(0, response.Code, "One weapon coating choice succeeds");
            AssertEqual($"{firstCoating.Id}:{firstCoating.TemplateId}:{firstCoating.Count}",
                string.Join(";", response.RewardGoodsList.Select(good => $"{good.Id}:{good.TemplateId}:{good.Count}")),
                "One weapon coating choice returns the selected coating");
            AssertEqual((int)RewardType.Item, response.RewardGoodsList.Single().RewardType, "Coating choice response type");
            AssertEqual(true, h.Session.character.WeaponFashions.Any(fashion => fashion.Id == firstFashion && fashion.ExpireTime == 0),
                "One weapon coating choice unlocks the fashion");
            AssertEqual(0L, Balance(h, 400076), "One weapon coating choice consumes the pack");
            AssertEqual(0L, Balance(h, firstCoating.TemplateId), "A coating choice does not leave the coating item in the bag");
        }

        using (MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
            out _, out _, out _))
        using (LoopbackSessionHarness h = new(CreateDrawCompatibilityCharacter(++uid), CreateDrawCompatibilityPlayer(uid),
            CreateDrawCompatibilityInventory(uid,
            [
                new Item { Id = 400076, Count = 1 },
                new Item { Id = firstCoating.TemplateId, Count = 1 }
            ]), "coating-already-held"))
        {
            h.Session.stage = CreateLoginAccountCompatibilityStage(uid);
            ItemUseResponse response = OpenChoice(h, new ItemUseRequest
            {
                Id = 400076, Count = 1, SelectRewardIds = [firstCoating.Id]
            });
            AssertEqual(0, response.Code, "A held coating item does not block the choice");
            AssertEqual(true, h.Session.character.WeaponFashions.Any(fashion => fashion.Id == firstFashion && fashion.ExpireTime == 0),
                "A held coating item still unlocks the fashion");
            AssertEqual(1L, Balance(h, firstCoating.TemplateId), "The held coating item stays at its cap");
            AssertEqual(0L, Balance(h, 400076), "A held coating item still consumes the pack");
        }

        using (MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
            out _, out _, out _))
        using (LoopbackSessionHarness h = new(CreateDrawCompatibilityCharacter(++uid), CreateDrawCompatibilityPlayer(uid),
            CreateDrawCompatibilityInventory(uid, [new Item { Id = 400076, Count = 2 }, new Item { Id = 40905, Count = 2 }]),
            "choice-stack"))
        {
            h.Session.stage = CreateLoginAccountCompatibilityStage(uid);
            ItemUseMultipleResponse coatings = OpenMultiple(h, new ItemUseMultipleRequest
            {
                UseList =
                [
                    new ItemUseMultipleEntry { Id = 400076, Count = 1, SelectRewardIds = [firstCoating.Id] },
                    new ItemUseMultipleEntry { Id = 400076, Count = 1, SelectRewardIds = [secondCoating.Id] }
                ]
            });
            AssertEqual(0, coatings.Code, "A coating stack opens through ItemUseMultipleRequest");
            AssertEqual($"{firstCoating.Id}:{firstCoating.TemplateId}:{firstCoating.Count};{secondCoating.Id}:{secondCoating.TemplateId}:{secondCoating.Count}",
                string.Join(";", coatings.RewardGoodsList.Select(good => $"{good.Id}:{good.TemplateId}:{good.Count}")),
                "A coating stack keeps each selected coating");
            AssertEqual(true, h.Session.character.WeaponFashions.Any(fashion => fashion.Id == firstFashion)
                && h.Session.character.WeaponFashions.Any(fashion => fashion.Id == secondFashion),
                "A coating stack unlocks both fashions");
            AssertEqual(0L, Balance(h, 400076), "A coating stack consumes both packs");

            ItemUseMultipleResponse shards = OpenMultiple(h, new ItemUseMultipleRequest
            {
                UseList =
                [
                    new ItemUseMultipleEntry { Id = 40905, Count = 1, SelectRewardIds = [firstShard.Id] },
                    new ItemUseMultipleEntry { Id = 40905, Count = 1, SelectRewardIds = [firstShard.Id] }
                ]
            });
            AssertEqual(0, shards.Code, "A shard stack opens through ItemUseMultipleRequest");
            AssertEqual($"{firstShard.Id}:{firstShard.TemplateId}:{2 * firstShard.Count}",
                string.Join(";", shards.RewardGoodsList.Select(good => $"{good.Id}:{good.TemplateId}:{good.Count}")),
                "A shard stack adds both copies");
            AssertEqual(2L * firstShard.Count, Balance(h, firstShard.TemplateId), "A shard stack grants both shards");
            AssertEqual(0L, Balance(h, 40905), "A shard stack consumes both packs");

            RejectMultipleUnchanged(h, new ItemUseMultipleRequest());
            RejectMultipleUnchanged(h, new ItemUseMultipleRequest
            {
                UseList = [new ItemUseMultipleEntry { Id = 400076, Count = 1, SelectRewardIds = [firstCoating.Id] }]
            });
            RejectMultipleUnchanged(h, new ItemUseMultipleRequest
            {
                UseList =
                [
                    new ItemUseMultipleEntry { Id = 400076, Count = 1, SelectRewardIds = [firstCoating.Id] },
                    new ItemUseMultipleEntry { Id = 40905, Count = 1, SelectRewardIds = [firstShard.Id] }
                ]
            });
            RejectMultipleUnchanged(h, new ItemUseMultipleRequest
            {
                UseList = [new ItemUseMultipleEntry { Id = 40905, Count = 1, SelectRewardIds = [1] }]
            });
        }

        long shardCap = Inventory.GetMaxCount(items[firstShard.TemplateId]);
        using (MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
            out _, out _, out _))
        using (LoopbackSessionHarness h = new(CreateDrawCompatibilityCharacter(++uid), CreateDrawCompatibilityPlayer(uid),
            CreateDrawCompatibilityInventory(uid,
            [
                new Item { Id = 40905, Count = 1 },
                new Item { Id = firstShard.TemplateId, Count = (int)shardCap }
            ]), "shard-full"))
        {
            h.Session.stage = CreateLoginAccountCompatibilityStage(uid);
            RejectUnchanged(h, new ItemUseRequest
            {
                Id = 40905, Count = 1, SelectRewardIds = [firstShard.Id]
            }, 20012005);
            RejectMultipleUnchanged(h, new ItemUseMultipleRequest
            {
                UseList = [new ItemUseMultipleEntry { Id = 40905, Count = 1, SelectRewardIds = [firstShard.Id] }]
            }, 20012005);
        }

        ItemTable invertPack = items[400026];
        RewardGoodsTable invertShard = goodsRows[rewardRows[invertPack.SubTypeParams[1]].SubIds[0]];
        using (MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
            out _, out _, out _))
        using (LoopbackSessionHarness h = new(CreateDrawCompatibilityCharacter(++uid), CreateDrawCompatibilityPlayer(uid),
            CreateDrawCompatibilityInventory(uid, [new Item { Id = 400026, Count = 1 }]), "invert-shard-pack"))
        {
            h.Session.stage = CreateLoginAccountCompatibilityStage(uid);
            ItemUseResponse response = OpenChoice(h, new ItemUseRequest
            {
                Id = 400026, Count = 1, SelectRewardIds = [invertShard.Id]
            });
            AssertEqual(0, response.Code, "S-Rank Inver-Shard Pack succeeds");
            AssertEqual($"{invertShard.Id}:{invertShard.TemplateId}:{invertShard.Count}",
                string.Join(";", response.RewardGoodsList.Select(good => $"{good.Id}:{good.TemplateId}:{good.Count}")),
                "S-Rank Inver-Shard Pack grants the selected shards");
            AssertEqual((long)invertShard.Count, Balance(h, invertShard.TemplateId), "S-Rank Inver-Shard Pack shard balance");
            AssertEqual(0L, Balance(h, 400026), "S-Rank Inver-Shard Pack consumes the pack");
        }

        long invertCap = Inventory.GetMaxCount(items[invertShard.TemplateId]);
        using (MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
            out _, out _, out _))
        using (LoopbackSessionHarness h = new(CreateDrawCompatibilityCharacter(++uid), CreateDrawCompatibilityPlayer(uid),
            CreateDrawCompatibilityInventory(uid,
            [
                new Item { Id = 400026, Count = 1 },
                new Item { Id = invertShard.TemplateId, Count = (int)invertCap }
            ]), "invert-shard-full"))
        {
            h.Session.stage = CreateLoginAccountCompatibilityStage(uid);
            RejectUnchanged(h, new ItemUseRequest
            {
                Id = 400026, Count = 1, SelectRewardIds = [invertShard.Id]
            }, 20012005);
        }

        AssertEqual("250:10;225:15;200:20;175:25;150:30",
            string.Join(";", ConstructResearchFortuneBagPolicy.Tiers.Select(tier => $"{tier.Count}:{tier.Weight}")),
            "Fortune bag odds are the item description");
        HashSet<int> fortuneCounts = ConstructResearchFortuneBagPolicy.Tiers.Select(tier => tier.Count).ToHashSet();
        using (MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
            out _, out _, out _))
        using (LoopbackSessionHarness h = new(CreateDrawCompatibilityCharacter(++uid), CreateDrawCompatibilityPlayer(uid),
            CreateDrawCompatibilityInventory(uid, [new Item { Id = 94033, Count = 1 }]), "fortune-bag"))
        {
            h.Session.stage = CreateLoginAccountCompatibilityStage(uid);
            ItemUseResponse response = OpenChoice(h, new ItemUseRequest { Id = 94033, Count = 1 });
            AssertEqual(0, response.Code, "Fortune bag succeeds");
            RewardGoods ticket = response.RewardGoodsList.Single();
            AssertEqual(ConstructResearchFortuneBagPolicy.TicketItemId, ticket.TemplateId, "Fortune bag grants Event Construct R&D Tickets");
            AssertEqual(true, fortuneCounts.Contains(ticket.Count), "Fortune bag count is one published tier");
            AssertEqual((long)ticket.Count, Balance(h, ticket.TemplateId), "Fortune bag ticket balance");
            AssertEqual(0L, Balance(h, 1), "Fortune bag does not pay Cogs");
            AssertEqual(0L, Balance(h, 2), "Fortune bag does not pay the free Black Card stack");
            AssertEqual(0L, Balance(h, 3), "Fortune bag does not pay the paid Black Card stack");
            AssertEqual(0L, Balance(h, 94033), "Fortune bag consumes the pack");
        }

        using (MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
            out _, out _, out _))
        using (LoopbackSessionHarness h = new(CreateDrawCompatibilityCharacter(++uid), CreateDrawCompatibilityPlayer(uid),
            CreateDrawCompatibilityInventory(uid, [new Item { Id = 400076, Count = 2 }]), "coating-pending"))
        {
            h.Session.stage = CreateLoginAccountCompatibilityStage(uid);
            h.Session.player.PendingItemUse = new ItemUsePendingOperation
            {
                ClaimKey = $"item-use:{uid}:coating",
                ItemId = 400076,
                Count = 2,
                SelectRewardIds = [firstCoating.Id, secondCoating.Id],
                Goods = [PendingGood(firstCoating), PendingGood(secondCoating)]
            };
            RejectMultipleUnchanged(h, new ItemUseMultipleRequest
            {
                UseList =
                [
                    new ItemUseMultipleEntry { Id = 400076, Count = 1, SelectRewardIds = [firstCoating.Id] },
                    new ItemUseMultipleEntry { Id = 400076, Count = 1, SelectRewardIds = [firstCoating.Id] }
                ]
            });
            ItemUseMultipleResponse resumed = OpenMultiple(h, new ItemUseMultipleRequest
            {
                UseList =
                [
                    new ItemUseMultipleEntry { Id = 400076, Count = 1, SelectRewardIds = [firstCoating.Id] },
                    new ItemUseMultipleEntry { Id = 400076, Count = 1, SelectRewardIds = [secondCoating.Id] }
                ]
            });
            AssertEqual(0, resumed.Code, "Matching coating stack retry succeeds");
            AssertEqual(true, h.Session.character.WeaponFashions.Any(fashion => fashion.Id == firstFashion)
                && h.Session.character.WeaponFashions.Any(fashion => fashion.Id == secondFashion),
                "Matching coating stack retry unlocks the stored coatings");
            AssertEqual(true, h.Session.player.PendingItemUse is null, "Matching coating stack retry clears pending");
        }

        // Reasons these packs stay closed, including the affection-gift boxes, are in Docs/closed.md.
        int[] closedPacks =
        [
            1050, 1051, 1052, 93000, 92000, 91006, 91000, 90101, 90110, 90107, 90108, 90104,
            400031, 400032, 400033, 400060, 400061, 400062, 40691, 40692, 40693, 60003
        ];
        using (MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
            out _, out _, out _))
        using (LoopbackSessionHarness h = new(CreateDrawCompatibilityCharacter(++uid), CreateDrawCompatibilityPlayer(uid),
            CreateDrawCompatibilityInventory(uid, closedPacks.Select(id => new Item { Id = id, Count = 1 }).ToList()),
            "closed-packs"))
        {
            foreach (int closedId in closedPacks)
                RejectUnchanged(h, new ItemUseRequest { Id = closedId, Count = 1 });
            RejectMultipleUnchanged(h, new ItemUseMultipleRequest
            {
                UseList = [new ItemUseMultipleEntry { Id = 40691, Count = 1 }]
            });
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
            && row.SubTypeParams[1] is not (1007 or 1008 or 1011)).Id;
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

        ItemUseMultipleResponse OpenMultiple(LoopbackSessionHarness h, ItemUseMultipleRequest request)
        {
            int id = ++packetId;
            InvokeRegisteredRequestHandler(nameof(ItemUseMultipleRequest), h.Session, id, request);
            ItemUseMultipleResponse? response = null;
            for (int index = 0; index < 32; index++)
            {
                Packet packet = h.ReadPacket("Multiple gift packet");
                if (packet.Type == Packet.ContentType.Push)
                {
                    Packet.Push push = MessagePackSerializer.Deserialize<Packet.Push>(packet.Content);
                    if (push.Name == nameof(NotifyItemDataList))
                    {
                        NotifyItemDataList notification = MessagePackSerializer.Deserialize<NotifyItemDataList>(push.Content);
                        AssertEqual(notification.ItemDataList.Count,
                            notification.ItemDataList.Select(item => item.Id).Distinct().Count(),
                            "Multiple gift item notification has one row per item");
                    }
                    continue;
                }
                AssertEqual(Packet.ContentType.Response, packet.Type, "Multiple gift terminates with response");
                Packet.Response envelope = MessagePackSerializer.Deserialize<Packet.Response>(packet.Content);
                AssertEqual(id, envelope.Id, "Multiple gift response identity");
                response = ReadResponsePayload<ItemUseMultipleResponse>(packet, nameof(ItemUseMultipleResponse));
                break;
            }
            AssertEqual(true, response is not null, "Multiple gift returns a response");
            AssertNoAvailablePacket(h, "Multiple gift has no extra packets");
            return response!;
        }

        void RejectMultipleUnchanged(LoopbackSessionHarness h, ItemUseMultipleRequest request, int? code = null)
        {
            byte[] player = h.Session.player.ToBson();
            byte[] inventory = h.Session.inventory.ToBson();
            byte[] character = h.Session.character.ToBson();
            int id = ++packetId;
            InvokeRegisteredRequestHandler(nameof(ItemUseMultipleRequest), h.Session, id, request);
            ItemUseMultipleResponse response = ReadResponsePayload<ItemUseMultipleResponse>(
                h, id, nameof(ItemUseMultipleResponse), "Multiple gift rejected without push");
            AssertEqual(true, response.Code != 0, "Multiple gift rejection code");
            if (code is int expected)
                AssertEqual(expected, response.Code, "Multiple gift rejection code");
            AssertEqual(0, response.RewardGoodsList.Count, "Rejected multiple gift exposes no granted goods");
            AssertNoAvailablePacket(h, "Rejected multiple gift emits no packets beyond response");
            AssertEqual(Convert.ToHexString(player), Convert.ToHexString(h.Session.player.ToBson()), "Rejected multiple gift leaves player unchanged");
            AssertEqual(Convert.ToHexString(inventory), Convert.ToHexString(h.Session.inventory.ToBson()), "Rejected multiple gift leaves inventory unchanged");
            AssertEqual(Convert.ToHexString(character), Convert.ToHexString(h.Session.character.ToBson()), "Rejected multiple gift leaves character unchanged");
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

        ItemUseResponse Reject(LoopbackSessionHarness h, ItemUseRequest request, int? code = null)
        {
            int id = ++packetId;
            InvokeRegisteredRequestHandler(nameof(ItemUseRequest), h.Session, id, request);
            ItemUseResponse response = ReadResponsePayload<ItemUseResponse>(h, id, nameof(ItemUseResponse), "Auto gift rejected without push");
            AssertEqual(true, response.Code != 0, "Auto gift rejection code");
            if (code is int expected)
                AssertEqual(expected, response.Code, "Auto gift rejection code");
            AssertEqual(0, response.RewardGoodsList.Count, "Rejected gift exposes no granted goods");
            AssertNoAvailablePacket(h, "Rejected gift emits no packets beyond response");
            return response;
        }

        void RejectUnchanged(LoopbackSessionHarness h, ItemUseRequest request, int? code = null)
        {
            byte[] player = h.Session.player.ToBson();
            byte[] inventory = h.Session.inventory.ToBson();
            byte[] character = h.Session.character.ToBson();
            Reject(h, request, code);
            AssertEqual(Convert.ToHexString(player), Convert.ToHexString(h.Session.player.ToBson()), "Rejected gift leaves player unchanged");
            AssertEqual(Convert.ToHexString(inventory), Convert.ToHexString(h.Session.inventory.ToBson()), "Rejected gift leaves inventory unchanged");
            AssertEqual(Convert.ToHexString(character), Convert.ToHexString(h.Session.character.ToBson()), "Rejected gift leaves character unchanged");
        }
    }
}
