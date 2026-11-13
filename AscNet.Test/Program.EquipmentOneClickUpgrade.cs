using AscNet.Common;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.equip;
using AscNet.Table.V2.share.item;
using MongoDB.Driver;

namespace AscNet.Test;

internal partial class Program
{
    private static void ValidateEquipmentOneClickUpgrade()
    {
        const long uid = 99_492;
        const int weaponId = 1;
        const int memoryOneId = 2;
        const int memoryTwoId = 3;
        const uint weaponTemplate = 2056001;
        const uint memoryOneTemplate = 3045071;
        const uint memoryTwoTemplate = 3045072;
        const int weaponEnhancerId = 31104;
        const int memoryEnhancerId = 31204;
        const int resonanceItemId = 3002;
        const int characterId = 1021001;
        const long initialCoins = 2_000_000;
        const int initialMaterials = 200;

        var originalPlayers = Player.collection;
        var originalCharacters = Character.collection;
        var originalInventory = Inventory.collection;
        var originalStages = Stage.collection;
        var client = Common.Common.db.Client;
        string databaseName = "ascnet_equipment_upgrade_test_" + Guid.NewGuid().ToString("N");
        var database = client.GetDatabase(databaseName);
        Player.collection = database.GetCollection<Player>("players");
        Character.collection = database.GetCollection<Character>("characters");
        Inventory.collection = database.GetCollection<Inventory>("inventory");
        Stage.collection = database.GetCollection<Stage>("stages");
        try
        {
            EquipBreakThroughTable Recipe(uint template, int phase) =>
                Character.ResolveEquipBreakThrough(template, phase)
                ?? throw new InvalidDataException($"Equipment {template} has no breakthrough recipe {phase}.");

            var weaponRecipe = Recipe(weaponTemplate, 2);
            var memoryRecipes = new[] { Recipe(memoryOneTemplate, 2), Recipe(memoryTwoTemplate, 2) };
            var resonance = TableReaderV2.Parse<EquipResonanceTable>().Single(row => row.Id == weaponTemplate);
            var skill = TableReaderV2.Parse<WeaponSkillPoolTable>().Single(row =>
                row.PoolId == resonance.WeaponSkillPoolId[0] && row.CharacterId == characterId).SkillId.First();
            var resonanceRecipe = TableReaderV2.Parse<EquipResonanceUseItemTable>().Single(row => row.Id == weaponTemplate);
            int resonanceCost = resonanceRecipe.ItemCount[resonanceRecipe.ItemId.IndexOf(resonanceItemId)];

            var player = CreateDrawCompatibilityPlayer(uid);
            var character = CreateDrawCompatibilityCharacter(uid);
            character.Characters.Add(CreateLoginAccountCompatibilityCharacter(characterId, fashionId: 3021001));
            character.Equips =
            [
                new() { Id = weaponId, TemplateId = weaponTemplate, CharacterId = characterId,
                    Breakthrough = weaponRecipe.Times, Level = weaponRecipe.LevelLimit },
                new() { Id = memoryOneId, TemplateId = memoryOneTemplate,
                    Breakthrough = memoryRecipes[0].Times, Level = memoryRecipes[0].LevelLimit - 1 },
                new() { Id = memoryTwoId, TemplateId = memoryTwoTemplate,
                    Breakthrough = memoryRecipes[1].Times, Level = memoryRecipes[1].LevelLimit }
            ];
            var balances = memoryRecipes.Append(weaponRecipe).SelectMany(row => row.ItemId)
                .Concat([weaponEnhancerId, memoryEnhancerId, resonanceItemId]).Distinct()
                .ToDictionary(id => id, _ => (long)initialMaterials);
            balances.Add(Inventory.Coin, initialCoins);
            var inventory = CreateDrawCompatibilityInventory(uid,
                balances.Select(row => new Item { Id = row.Key, Count = row.Value }));
            var stage = new Stage { Uid = uid, Stages = new() };
            Player.collection.InsertOne(player);
            Character.collection.InsertOne(character);
            Inventory.collection.InsertOne(inventory);
            Stage.collection.InsertOne(stage);

            int packetId = 49_250;
            var upgraded = new Dictionary<int, (int Breakthrough, int Level, int Exp)>();
            using (LoopbackSessionHarness session = new(character, player, inventory, "equipment-upgrade-test", startClientLoop: false))
            {
                session.Session.stage = stage;
                TResponse Call<TResponse>(string requestName, object request)
                {
                    InvokeRegisteredRequestHandler(requestName, session.Session, ++packetId, request);
                    return ReadResponsePayload<TResponse>(session, packetId, typeof(TResponse).Name,
                        requestName, maxPacketsToRead: 12);
                }

                var items = TableReaderV2.Parse<ItemTable>().ToDictionary(row => row.Id);
                var memoryEnhancer = items[memoryEnhancerId].GetEquipUpgradeInfo();
                int singleCount = (character.GetEquipExpRequiredToReach(memoryOneId, memoryRecipes[0].LevelLimit)
                    + memoryEnhancer.Exp - 1) / memoryEnhancer.Exp;
                EquipLevelUpResponse single = Call<EquipLevelUpResponse>(nameof(EquipLevelUpRequest),
                    new EquipLevelUpRequest { EquipId = memoryOneId, UseItems = new() { [memoryEnhancerId] = singleCount } });
                AssertEqual(0, single.Code, "Single Memory enhancement succeeds");
                AssertEqual(memoryRecipes[0].LevelLimit, single.Level, "Single Memory reaches the current cap");
                balances[memoryEnhancerId] -= singleCount;
                balances[Inventory.Coin] -= singleCount * memoryEnhancer.Cost;

                void Enhance(int equipId, EquipBreakThroughTable recipe, int enhancerId)
                {
                    const int count = 2;
                    var enhancer = items[enhancerId].GetEquipUpgradeInfo();
                    var nextRecipe = Recipe(checked((uint)recipe.EquipId), recipe.Times + 1);
                    int exp = count * enhancer.Exp;
                    int level = 1;
                    foreach (var step in Character.equipLevelUpTemplates
                        .Where(row => row.TemplateId == nextRecipe.LevelUpTemplateId && row.Level < nextRecipe.LevelLimit)
                        .OrderBy(row => row.Level))
                    {
                        if (exp < step.Exp) break;
                        exp -= step.Exp;
                        level++;
                    }

                    EquipOneKeyFeedResponse response = Call<EquipOneKeyFeedResponse>(nameof(EquipOneKeyFeedRequest),
                        new EquipOneKeyFeedRequest
                        {
                            EquipId = equipId, TargetBreakthrough = nextRecipe.Times, TargetLevel = level,
                            OperationInfos =
                            [
                                new() { OperationType = 2 },
                                new() { OperationType = 1, UseItemIdList = [enhancerId], UseItemCountList = [count] }
                            ]
                        });
                    AssertEqual(0, response.Code, $"One-click upgrade succeeds for equip {equipId}");
                    AssertEqual((nextRecipe.Times, level, exp), (response.Breakthrough, response.Level, response.Exp),
                        $"Equip {equipId} advances through its breakthrough and feeds the requested EXP");
                    AssertEqual(2, response.SuccessTimes, $"Equip {equipId} completes both operations");
                    upgraded.Add(equipId, (nextRecipe.Times, level, exp));
                    foreach ((int id, int amount) in recipe.ItemId.Zip(recipe.ItemCount))
                        balances[id] -= amount;
                    balances[recipe.UseItemId] -= recipe.UseMoney;
                    balances[enhancerId] -= count;
                    balances[Inventory.Coin] -= count * enhancer.Cost;
                }

                // The client sends one EquipOneKeyFeedRequest per Memory in a bulk operation.
                Enhance(memoryOneId, memoryRecipes[0], memoryEnhancerId);
                Enhance(memoryTwoId, memoryRecipes[1], memoryEnhancerId);
                Enhance(weaponId, weaponRecipe, weaponEnhancerId);

                EquipResonanceResponse selection = Call<EquipResonanceResponse>(nameof(EquipResonanceRequest),
                    new EquipResonanceRequest
                    {
                        EquipId = weaponId, Slots = [1], CharacterId = characterId,
                        UseItemId = resonanceItemId, SelectSkillIds = [skill], SelectType = EquipResonanceType.WeaponSkill
                    });
                AssertEqual(0, selection.Code, "One-click weapon resonance selection succeeds");
                AssertEqual(skill, selection.ResonanceDatas.Single().TemplateId, "The selected weapon skill is returned");
                balances[resonanceItemId] -= resonanceCost;
            }

            // Reopen a session from the persisted BSON documents after disposing the original connection.
            using LoopbackSessionHarness reloaded = new(Character.collection.Find(row => row.Uid == uid).Single(),
                Player.collection.Find(row => row.PlayerData.Id == uid).Single(),
                Inventory.collection.Find(row => row.Uid == uid).Single(), "equipment-upgrade-reload-test", startClientLoop: false);
            AssertEqual(upgraded.Count, reloaded.Session.character.Equips.Count, "Equipment inventory has no duplicate or unintended grants");
            foreach (EquipData equip in reloaded.Session.character.Equips)
                AssertEqual(upgraded[(int)equip.Id], (equip.Breakthrough, equip.Level, equip.Exp),
                    $"Equip {equip.Id} has the same progression in the new session");
            EquipData reloadedWeapon = reloaded.Session.character.Equips.Single(equip => equip.Id == weaponId);
            AssertEqual(skill, reloadedWeapon.ResonanceInfo.Single().TemplateId, "Weapon resonance persists in the new session");
            var reloadedBalances = reloaded.Session.inventory.Items.ToDictionary(item => item.Id, item => item.Count);
            AssertEqual(balances.Count, reloadedBalances.Count, "Item inventory has no unintended rewards");
            foreach ((int itemId, long remaining) in balances)
                AssertEqual(remaining, reloadedBalances[itemId], $"Item {itemId} retains the exact debited balance");
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
