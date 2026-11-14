using AscNet.Common;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.character;
using AscNet.Table.V2.share.equip;
using AscNet.Table.V2.share.item;
using AscNet.Table.V2.share.partner;
using AscNet.Table.V2.share.partner.leveluptemplate;
using MongoDB.Driver;

namespace AscNet.Test;

internal partial class Program
{
    // Issue #67: a legacy weapon repair at login must not unequip the worn Memories, and a bulk
    // EquipOneKeyFeedRequest over two Memories must debit exactly and persist.
    private static void ValidateMemoryLoginAndBulkFeed()
    {
        const long uid = 99_493;
        const int characterId = 1021001; // Lucia: EquipType 2, default weapon 2022001
        const int memoryEnhancerId = 31204;

        var equipRows = TableReaderV2.Parse<EquipTable>().Where(Character.IsOwnableEquipTemplate).ToList();
        var characterRow = TableReaderV2.Parse<CharacterTable>().Single(row => row.Id == characterId);
        EquipTable legacyWeapon = equipRows.First(row => row.Site == 0 && row.Type != characterRow.EquipType);
        EquipTable[] memories = Enumerable.Range(1, 6).Select(site => equipRows.First(row => row.Site == site && row.Type == 0
            && Character.ResolveEquipBreakThrough((uint)row.Id, 2) is not null
            && Character.ResolveEquipBreakThrough((uint)row.Id, 3) is not null)).ToArray();

        var originalPlayers = Player.collection;
        var originalCharacters = Character.collection;
        var originalInventory = Inventory.collection;
        var originalStages = Stage.collection;
        var client = Common.Common.db.Client;
        string databaseName = "ascnet_manual_full_i67m_" + Guid.NewGuid().ToString("N")[..12];
        var database = client.GetDatabase(databaseName);
        Player.collection = database.GetCollection<Player>("players");
        Character.collection = database.GetCollection<Character>("characters");
        Inventory.collection = database.GetCollection<Inventory>("inventory");
        Stage.collection = database.GetCollection<Stage>("stages");
        try
        {
            var player = CreateDrawCompatibilityPlayer(uid);
            var character = CreateDrawCompatibilityCharacter(uid);
            character.Characters.Add(CreateLoginAccountCompatibilityCharacter(characterId, fashionId: 3021001));
            character.Equips = [new() { Id = 1, TemplateId = (uint)legacyWeapon.Id, CharacterId = characterId }];
            for (int i = 0; i < memories.Length; i++)
                character.Equips.Add(new() { Id = (uint)(i + 2), TemplateId = (uint)memories[i].Id, CharacterId = characterId });
            // Memories 1 and 2 sit at a breakthrough cap so the bulk request can break through and feed.
            var recipes = new[] { 0, 1 }.Select(i => Character.ResolveEquipBreakThrough((uint)memories[i].Id, 2)!).ToArray();
            for (int i = 0; i < 2; i++)
            {
                character.Equips[i + 1].Breakthrough = recipes[i].Times;
                character.Equips[i + 1].Level = recipes[i].LevelLimit;
            }

            character.NormalizeCharactersForCurrentTables([]);
            AssertEqual(true, character.Equips.Where(equip => memories.Any(row => row.Id == equip.TemplateId))
                .All(equip => equip.CharacterId == characterId), "A legacy weapon repair keeps all six worn Memories");
            AssertEqual(6, character.Equips.Count(equip => equip.CharacterId == characterId
                && memories.Any(row => row.Id == equip.TemplateId)), "All six Memory assignments survive login normalization");
            AssertEqual(0, character.Equips.Single(equip => equip.TemplateId == (uint)legacyWeapon.Id).CharacterId,
                "The incompatible legacy weapon is unequipped");
            AssertEqual(characterId, character.Equips.Single(equip => equip.TemplateId == (uint)characterRow.EquipId).CharacterId,
                "The character's default weapon is equipped instead");

            var items = TableReaderV2.Parse<ItemTable>().ToDictionary(row => row.Id);
            var enhancer = items[memoryEnhancerId].GetEquipUpgradeInfo();
            var balance = recipes.SelectMany(row => row.ItemId).Append(memoryEnhancerId).Append(Inventory.Coin)
                .Concat(recipes.Select(row => row.UseItemId)).Distinct().ToDictionary(id => id, _ => 50_000L);
            var inventory = CreateDrawCompatibilityInventory(uid, balance.Select(row => new Item { Id = row.Key, Count = row.Value }));
            var stage = new Stage { Uid = uid, Stages = new() };
            Player.collection.InsertOne(player);
            Character.collection.InsertOne(character);
            Inventory.collection.InsertOne(inventory);
            Stage.collection.InsertOne(stage);

            int packetId = 49_300;
            var expected = new Dictionary<int, (int Breakthrough, int Level, int Exp)>();
            using (LoopbackSessionHarness session = new(character, player, inventory, "issue67-memory-test", startClientLoop: false))
            {
                session.Session.stage = stage;
                // The client sends one EquipOneKeyFeedRequest per eligible Memory in a bulk upgrade.
                for (int i = 0; i < 2; i++)
                {
                    var recipe = recipes[i];
                    var next = Character.ResolveEquipBreakThrough((uint)recipe.EquipId, recipe.Times + 1)!;
                    const int count = 2;
                    int exp = count * enhancer.Exp, level = 1;
                    foreach (var step in Character.equipLevelUpTemplates
                        .Where(row => row.TemplateId == next.LevelUpTemplateId && row.Level < next.LevelLimit).OrderBy(row => row.Level))
                    {
                        if (exp < step.Exp) break;
                        exp -= step.Exp;
                        level++;
                    }
                    int equipId = i + 2;
                    InvokeRegisteredRequestHandler(nameof(EquipOneKeyFeedRequest), session.Session, ++packetId,
                        new EquipOneKeyFeedRequest
                        {
                            EquipId = equipId, TargetBreakthrough = next.Times, TargetLevel = level,
                            OperationInfos =
                            [
                                new() { OperationType = 2 },
                                new() { OperationType = 1, UseItemIdList = [memoryEnhancerId], UseItemCountList = [count] }
                            ]
                        });
                    var response = ReadResponsePayload<EquipOneKeyFeedResponse>(session, packetId,
                        nameof(EquipOneKeyFeedResponse), "Bulk Memory feed", maxPacketsToRead: 12);
                    AssertEqual(0, response.Code, $"Bulk feed succeeds for Memory {equipId}");
                    AssertEqual((next.Times, level, exp), (response.Breakthrough, response.Level, response.Exp), $"Memory {equipId} progression");
                    expected[equipId] = (next.Times, level, exp);
                    foreach ((int id, int amount) in recipe.ItemId.Zip(recipe.ItemCount))
                        balance[id] -= amount;
                    balance[recipe.UseItemId] -= recipe.UseMoney;
                    balance[memoryEnhancerId] -= count;
                    balance[Inventory.Coin] -= count * enhancer.Cost;
                }
            }

            using LoopbackSessionHarness reloaded = new(Character.collection.Find(row => row.Uid == uid).Single(),
                Player.collection.Find(row => row.PlayerData.Id == uid).Single(),
                Inventory.collection.Find(row => row.Uid == uid).Single(), "issue67-memory-reload", startClientLoop: false);
            foreach (var equip in reloaded.Session.character.Equips.Where(equip => memories.Any(row => row.Id == equip.TemplateId)))
                AssertEqual(characterId, equip.CharacterId, $"Memory {equip.Id} stays worn after relog");
            foreach ((int equipId, var progress) in expected)
            {
                var equip = reloaded.Session.character.Equips.Single(row => row.Id == equipId);
                AssertEqual(progress, (equip.Breakthrough, equip.Level, equip.Exp), $"Memory {equipId} progression persists");
            }
            var persisted = reloaded.Session.inventory.Items.ToDictionary(item => item.Id, item => item.Count);
            foreach ((int itemId, long remaining) in balance)
                AssertEqual(remaining, persisted.GetValueOrDefault(itemId), $"Item {itemId} keeps the exact debited balance");
        }
        finally
        {
            Player.collection = originalPlayers;
            Character.collection = originalCharacters;
            Inventory.collection = originalInventory;
            Stage.collection = originalStages;
            client.DropDatabase(databaseName);
        }
    }

    // Issue #67: CUB Quick Upgrade sends PartnerLevelUpRequest with every EXP material the authored
    // PartnerDecomposeExpItemRebate list (30111|30112|30113) names.
    private static void ValidatePartnerQuickUpgrade()
    {
        const long uid = 99_494;
        const int partnerTemplate = 16010000;
        var items = TableReaderV2.Parse<ItemTable>().ToDictionary(row => row.Id);
        int[] authored = (TableReaderV2.Parse<AscNet.Table.V2.share.config.ConfigTable>()
            .Single(row => row.Key == "PartnerDecomposeExpItemRebate").Value).Split('|').Select(int.Parse).ToArray();
        AssertIntegerList([30111, 30112, 30113], authored.Select(id => (long)id).ToArray(), "Authored CUB EXP material list");
        (int Exp, int Coin) Cost(int id) => (items[id].SubTypeParams[0], items[id].SubTypeParams[1]);
        var phases = TableReaderV2.Parse<PartnerBreakThroughTable>().Where(row => row.PartnerId == partnerTemplate)
            .OrderBy(row => row.BreakTimes).ToArray();
        List<(int Level, int AllExp)> Levels(int template) => template switch
        {
            501 => TableReaderV2.Parse<PartnerLevelUpTemplate501Table>().Select(row => (row.Level, row.AllExp)).ToList(),
            502 => TableReaderV2.Parse<PartnerLevelUpTemplate502Table>().Select(row => (row.Level, row.AllExp)).ToList(),
            503 => TableReaderV2.Parse<PartnerLevelUpTemplate503Table>().Select(row => (row.Level, row.AllExp)).ToList(),
            _ => TableReaderV2.Parse<PartnerLevelUpTemplate504Table>().Select(row => (row.Level, row.AllExp)).ToList()
        };

        var originalPlayers = Player.collection;
        var originalCharacters = Character.collection;
        var originalInventory = Inventory.collection;
        var originalStages = Stage.collection;
        var client = Common.Common.db.Client;
        string databaseName = "ascnet_manual_full_i67p_" + Guid.NewGuid().ToString("N")[..12];
        var database = client.GetDatabase(databaseName);
        Player.collection = database.GetCollection<Player>("players");
        Character.collection = database.GetCollection<Character>("characters");
        Inventory.collection = database.GetCollection<Inventory>("inventory");
        Stage.collection = database.GetCollection<Stage>("stages");
        try
        {
            var player = CreateDrawCompatibilityPlayer(uid);
            var character = CreateDrawCompatibilityCharacter(uid);
            character.Partners =
            [
                new PartnerData
                {
                    Id = 1, TemplateId = partnerTemplate, Level = 1, Quality = 1,
                    SkillList = [new PartnerSkillData { Id = 1, Type = 1, Level = 1 }, new PartnerSkillData { Id = 2, Type = 2, Level = 1 }]
                }
            ];
            var balance = new Dictionary<int, long> { [Inventory.Coin] = 10_000_000, [30111] = 50, [30112] = 500, [30113] = 500, [30114] = 5 };
            foreach (var phase in phases.Where(row => row.CostItemId.Count > 0))
                foreach ((int id, int count) in phase.CostItemId.Zip(phase.CostItemCount))
                    balance[id] = balance.GetValueOrDefault(id) + count * 2L;
            var inventory = CreateDrawCompatibilityInventory(uid, balance.Select(row => new Item { Id = row.Key, Count = row.Value }));
            var stage = new Stage { Uid = uid, Stages = new() };
            Player.collection.InsertOne(player);
            Character.collection.InsertOne(character);
            Inventory.collection.InsertOne(inventory);
            Stage.collection.InsertOne(stage);

            int packetId = 49_400;
            using (LoopbackSessionHarness session = new(character, player, inventory, "issue67-partner-test", startClientLoop: false))
            {
                session.Session.stage = stage;
                var partner = character.Partners.Single();
                PartnerLevelUpResponse LevelUp(Dictionary<int, int> use)
                {
                    InvokeRegisteredRequestHandler(nameof(PartnerLevelUpRequest), session.Session, ++packetId,
                        new PartnerLevelUpRequest { PartnerId = 1, UseItems = use });
                    return ReadResponsePayload<PartnerLevelUpResponse>(session, packetId, nameof(PartnerLevelUpResponse),
                        "CUB Quick Upgrade", maxPacketsToRead: 12);
                }
                void AssertRejected(Dictionary<int, int> use, string label)
                {
                    var before = balance.ToDictionary(row => row.Key, row => session.Session.inventory.Items
                        .FirstOrDefault(item => item.Id == row.Key)?.Count ?? 0);
                    var state = (partner.Level, partner.Exp, partner.BreakThrough);
                    AssertEqual(1, LevelUp(use).Code, label);
                    AssertEqual(state, (partner.Level, partner.Exp, partner.BreakThrough), $"{label}: partner unchanged");
                    foreach ((int id, long count) in before)
                        AssertEqual(count, session.Session.inventory.Items.FirstOrDefault(item => item.Id == id)?.Count ?? 0,
                            $"{label}: item {id} unchanged");
                }
                void Spend(Dictionary<int, int> use)
                {
                    foreach ((int id, int count) in use)
                    {
                        balance[id] -= count;
                        balance[Inventory.Coin] -= (long)count * Cost(id).Coin;
                    }
                }
                (int Level, int Exp) Expect(PartnerBreakThroughTable phase, int startLevel, int startExp, Dictionary<int, int> use)
                {
                    var table = Levels(phase.LevelUpTemplateId);
                    int total = table.First(row => row.Level == startLevel).AllExp + startExp + use.Sum(row => Cost(row.Key).Exp * row.Value);
                    int level = Math.Min(table.Where(row => row.AllExp <= total).Max(row => row.Level), phase.LevelLimit);
                    return (level, level == phase.LevelLimit ? 0 : total - table.First(row => row.Level == level).AllExp);
                }

                // Mixed 30111 + 30112 (the authored list), below the phase cap.
                Dictionary<int, int> mixed = new() { [30111] = 2, [30112] = 1 };
                var wanted = Expect(phases[0], 1, 0, mixed);
                var response = LevelUp(mixed);
                AssertEqual(0, response.Code, "Mixed 30111 + 30112 is accepted");
                AssertEqual(wanted, (response.Level, response.Exp), "Mixed materials grant the Item.tsv EXP");
                AssertEqual(true, wanted.Level > 1, "Mixed materials advance the partner");
                Spend(mixed);

                // Rejections validate everything before any mutation.
                AssertRejected(new() { [30114] = 1 }, "An unlisted material is rejected");
                AssertRejected(new() { [30111] = 1, [30114] = 1 }, "An unlisted material among valid ones rejects the whole request");
                AssertRejected(new() { [30112] = 999 }, "Insufficient materials are rejected");
                AssertRejected(new() { [30111] = 1, [30112] = 0 }, "A zero-count entry is rejected");
                AssertRejected([], "An empty request is rejected");
                var coin = session.Session.inventory.Items.Single(item => item.Id == Inventory.Coin);
                long coinBalance = coin.Count;
                coin.Count = Cost(30112).Coin - 1;
                AssertRejected(new() { [30112] = 1 }, "Insufficient coins are rejected");
                coin.Count = coinBalance;

                bool sawPartial = false;
                // Phase transitions: overfeeding stops at each phase cap, then PartnerBreakThrough opens the next phase.
                foreach (var phase in phases.Where(row => row.BreakTimes < phases[^1].BreakTimes))
                {
                    if (partner.Level < phase.LevelLimit)
                    {
                        // A partial mixed feed keeps the carry-over EXP below the cap in at least one phase.
                        Dictionary<int, int> partial = new() { [30111] = 1, [30112] = 1 };
                        var partialWanted = Expect(phase, partner.Level, partner.Exp, partial);
                        response = LevelUp(partial);
                        AssertEqual(0, response.Code, $"Phase {phase.BreakTimes} partial mixed feed succeeds");
                        AssertEqual(partialWanted, (response.Level, response.Exp), $"Phase {phase.BreakTimes} partial feed matches the table");
                        Spend(partial);
                        sawPartial |= partialWanted.Level < phase.LevelLimit;
                    }
                    if (partner.Level < phase.LevelLimit)
                    {
                        Dictionary<int, int> feed = new() { [30112] = 40, [30113] = 40 };
                        int startLevel = partner.Level, startExp = partner.Exp;
                        wanted = Expect(phase, startLevel, startExp, feed);
                        response = LevelUp(feed);
                        AssertEqual(0, response.Code, $"Phase {phase.BreakTimes} feed succeeds");
                        AssertEqual((phase.LevelLimit, 0), (response.Level, response.Exp), $"Phase {phase.BreakTimes} stops at its cap");
                        AssertEqual(wanted, (response.Level, response.Exp), $"Phase {phase.BreakTimes} matches the table");
                        Spend(feed);
                    }
                    AssertRejected(new() { [30111] = 1 }, $"Phase {phase.BreakTimes} cap rejects further EXP");

                    InvokeRegisteredRequestHandler(nameof(PartnerBreakThroughRequest), session.Session, ++packetId,
                        new PartnerBreakThroughRequest { PartnerId = 1 });
                    var broke = ReadResponsePayload<PartnerBreakThroughResponse>(session, packetId,
                        nameof(PartnerBreakThroughResponse), "CUB breakthrough", maxPacketsToRead: 12);
                    AssertEqual(0, broke.Code, $"Phase {phase.BreakTimes} breakthrough succeeds");
                    foreach ((int id, int count) in phase.CostItemId.Zip(phase.CostItemCount))
                        balance[id] -= count;
                }
                AssertEqual(true, sawPartial, "A mixed feed stayed below a phase cap");
                var last = phases[^1];
                var finalFeed = new Dictionary<int, int> { [30113] = 100 };
                response = LevelUp(finalFeed);
                AssertEqual(0, response.Code, "The final phase feed succeeds");
                AssertEqual((30, 0), (response.Level, response.Exp), "Quick Upgrade reaches Lv.30");
                AssertEqual((phases[^1].BreakTimes, last.LevelLimit), (partner.BreakThrough, partner.Level), "Final phase and cap");
                Spend(finalFeed);
            }

            using LoopbackSessionHarness reloaded = new(Character.collection.Find(row => row.Uid == uid).Single(),
                Player.collection.Find(row => row.PlayerData.Id == uid).Single(),
                Inventory.collection.Find(row => row.Uid == uid).Single(), "issue67-partner-reload", startClientLoop: false);
            var persistedPartner = reloaded.Session.character.Partners.Single();
            AssertEqual((phases[^1].BreakTimes, 30, 0), (persistedPartner.BreakThrough, persistedPartner.Level, persistedPartner.Exp),
                "Partner progression persists");
            var persisted = reloaded.Session.inventory.Items.ToDictionary(item => item.Id, item => item.Count);
            foreach ((int itemId, long remaining) in balance)
                AssertEqual(remaining, persisted.GetValueOrDefault(itemId), $"Item {itemId} keeps the exact debited balance");
        }
        finally
        {
            Player.collection = originalPlayers;
            Character.collection = originalCharacters;
            Inventory.collection = originalInventory;
            Stage.collection = originalStages;
            client.DropDatabase(databaseName);
        }
    }
}
