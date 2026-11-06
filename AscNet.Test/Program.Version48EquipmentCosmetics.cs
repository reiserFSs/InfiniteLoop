using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.equip;
using AscNet.Table.V2.share.fashion;
using AscNet.Table.V2.share.headportrait;

namespace AscNet.Test;

internal static partial class Program
{
    private static void ValidateVersion48EquipmentCosmetics()
    {
        EquipTable pesanteur = TableReaderV2.Parse<EquipTable>()
            .Single(row => row.Name == "Pesanteur" && row.Site == 1);
        EquipTable priorMemory = TableReaderV2.Parse<EquipTable>()
            .First(row => row.Type == 0 && row.Site == 1 && row.SuitId != pesanteur.SuitId && row.Quality == 6);
        FashionTable newCoating = TableReaderV2.Parse<FashionTable>()
            .Single(row => row.Name == "Calamity's End");
        HeadPortraitTable portrait = TableReaderV2.Parse<HeadPortraitTable>()
            .Single(row => row.Name == "Portrait - Anabasis" && row.Type == 1);
        HeadPortraitTable frame = TableReaderV2.Parse<HeadPortraitTable>()
            .Single(row => row.Name == "Portrait Frame - Lingering Melody" && row.Type == 2);
        const int characterId = 1421003;
        const long uid = 99_848;
        Character character = new()
        {
            Uid = uid,
            Characters = [new CharacterData { Id = characterId }],
            Equips =
            [
                new EquipData { Id = 84801, TemplateId = (uint)priorMemory.Id },
                new EquipData { Id = 84802, TemplateId = (uint)pesanteur.Id }
            ],
            Fashions = [new FashionList { Id = newCoating.Id, IsLock = false }]
        };
        Player player = CreateDrawCompatibilityPlayer(uid);
        player.HeadPortraits.AddRange(
        [
            new HeadPortraitList { Id = portrait.Id, LeftCount = 1 },
            new HeadPortraitList { Id = frame.Id, LeftCount = 1 }
        ]);
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
            out _, out RecordingMongoCollectionProxy<Character> characters, out _);
        Inventory inventory = CreateDrawCompatibilityInventory(uid,
            [new Item { Id = 62738, Count = 2230 }]);
        using LoopbackSessionHarness harness = new(character, player, inventory);
        foreach (EquipData equip in character.Equips)
        {
            int id = checked((int)equip.Id);
            InvokeRequestHandler(harness, nameof(EquipPutOnRequest), id,
                new EquipPutOnRequest { CharacterId = characterId, EquipId = id, Site = 1 });
            NotifyEquipDataList updated = ReadPushPayload<NotifyEquipDataList>(
                harness, nameof(NotifyEquipDataList), "complete memory slot update");
            AssertEqual(characterId, updated.EquipDataList.Single(row => row.Id == equip.Id).CharacterId,
                "the selected memory reaches the client before its response");
            if (id == 84802)
                AssertEqual(0, updated.EquipDataList.Single(row => row.Id == 84801).CharacterId,
                    "the same push removes the displaced memory");
            EquipPutOnResponse response = ReadResponsePayload<EquipPutOnResponse>(
                harness, id, nameof(EquipPutOnResponse), $"memory {equip.TemplateId} equip", maxPacketsToRead: 8);
            AssertEqual(0, response.Code, $"memory {equip.TemplateId} equips");
            AssertEqual(1, character.Equips.Count(row => row.CharacterId == characterId),
                "only the selected memory remains equipped in site 1");
            AssertEqual(characterId, equip.CharacterId, $"memory {equip.TemplateId} selected");
        }
        AssertEqual(true, characters.ReplaceOneCalls >= 2, "two distinct memory equips persist");

        EquipConfigTable[] discounts = TableReaderV2.Parse<EquipConfigTable>()
            .Where(row => row.ItemId == 62738).ToArray();
        AssertEqual(3, discounts.Length, "three authored resonance discounts (4.8 suits 1665/1613 plus 4.9 suit 1666)");
        AssertEqual(true, discounts.All(row => row.DiscountCount == 539), "authored discounted material count");
        int[] suitIds = [..discounts.Select(row => row.SuitId), 1664];
        int[] costs = [..discounts.Select(row => row.DiscountCount), 613];
        for (int index = 0; index < suitIds.Length; index++)
        {
            EquipTable memory = TableReaderV2.Parse<EquipTable>()
                .Single(row => row.Site == 1 && row.SuitId == suitIds[index]);
            EquipResonanceUseItemTable recipe = TableReaderV2.Parse<EquipResonanceUseItemTable>()
                .Single(row => row.Id == memory.Id);
            int materialIndex = recipe.ItemId.IndexOf(62738);
            AssertEqual(true, materialIndex >= 0, "memory has authored resonance material");
            AssertEqual(613, recipe.ItemCount[materialIndex], "unmodified recipe cost");
            EquipData owned = new() { Id = (uint)(84810 + index), TemplateId = (uint)memory.Id };
            character.Equips.Add(owned);
            long balance = inventory.Items.Single(item => item.Id == 62738).Count;
            InvokeRequestHandler(harness, nameof(EquipResonanceRequest), 84810 + index,
                new EquipResonanceRequest
                {
                    EquipId = (int)owned.Id, Slots = [1], CharacterId = characterId,
                    UseItemId = 62738
                });
            EquipResonanceResponse resonance = ReadResponsePayload<EquipResonanceResponse>(
                harness, 84810 + index, nameof(EquipResonanceResponse), $"memory {memory.Name} resonance", maxPacketsToRead: 8);
            AssertEqual(0, resonance.Code, $"memory {memory.Name} resonance succeeds");
            AssertEqual(balance - costs[index], inventory.Items.Single(item => item.Id == 62738).Count,
                $"memory {memory.Name} pays configured cost");
            AssertEqual(1, owned.ResonanceInfo.Count, $"memory {memory.Name} commits resonance");
        }

        InvokeRequestHandler(harness, nameof(FashionUseRequest), 84803,
            new FashionUseRequest { FashionId = checked((uint)newCoating.Id) });
        FashionUseResponse coatingResponse = ReadResponsePayload<FashionUseResponse>(
            harness, 84803, nameof(FashionUseResponse), "Calamity's End equip", maxPacketsToRead: 8);
        AssertEqual(0, coatingResponse.Code, "owned 4.8 coating equips");
        AssertEqual((long)newCoating.Id, character.Characters.Single().FashionId, "coating saved on Adelyde");

        InvokeRequestHandler(harness, nameof(SetHeadPortraitRequest), 84804,
            new SetHeadPortraitRequest { Id = portrait.Id });
        AssertEqual(0, ReadResponsePayload<SetHeadPortraitResponse>(
            harness, 84804, nameof(SetHeadPortraitResponse), "Anabasis portrait equip").Code,
            "owned 4.8 portrait equips");
        AssertEqual((long)portrait.Id, player.PlayerData.CurrHeadPortraitId, "Anabasis portrait persists");
        InvokeRequestHandler(harness, nameof(SetHeadFrameRequest), 84805,
            new SetHeadFrameRequest { Id = frame.Id });
        AssertEqual(0, ReadResponsePayload<SetHeadFrameResponse>(
            harness, 84805, nameof(SetHeadFrameResponse), "Lingering Melody frame equip").Code,
            "owned 4.8 frame equips");
        AssertEqual((long)frame.Id, player.PlayerData.CurrHeadFrameId, "4.8 frame persists");
    }
}
