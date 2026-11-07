using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Commands;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.item;
using System.Reflection;

namespace AscNet.Test;

internal static partial class Program
{
    /// <summary>
    /// Weapon coating ticket items (ItemType 32, e.g. CN 绯色慕意 item 80148) map to WeaponFashion.Id
    /// (12700001) via SubTypeParams[0]. The client only knows the fashion id: ownership, wear and the
    /// login list must carry it, and the ticket must never sit in the bag.
    /// </summary>
    private static void ValidateWeaponCoatingIdSpace()
    {
        PacketFactory.LoadPacketHandlers();
        List<ItemTable> tickets = TableReaderV2.Parse<ItemTable>()
            .Where(row => row.ItemType == (int)AscNet.Common.ItemType.WeaponFashion).ToList();
        AssertEqual(80148, tickets.Single(row => row.SubTypeParams[0] == 12700001).Id, "Coating 12700001 comes from item 80148");
        AssertEqual(tickets.Count, tickets.Select(row => row.SubTypeParams[0]).Distinct().Count(), "Coating tickets map to distinct fashion ids");

        ItemTable first = tickets.Single(row => row.Id == 80148);
        ItemTable second = tickets.First(row => row.Id != 80148 && row.SubTypeParams[0] != 12700001);
        const long uid = 99_870;
        const uint characterId = 1021001;
        CommandFactory.commands.Clear();
        CommandFactory.LoadCommands();
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForBiancaCompatibility(out _, out _, out _, out _);
        AscNet.Common.Database.Character character = CreateDrawCompatibilityCharacter(uid);
        character.Characters = [new CharacterData { Id = characterId }];
        using LoopbackSessionHarness h = new(character, CreateDrawCompatibilityPlayer(uid), CreateDrawCompatibilityInventory(uid, []), "weapon-coating-ids");
        h.Session.stage = CreateLoginAccountCompatibilityStage(uid);

        foreach (ItemTable ticket in new[] { first, second })
        {
            CommandFactory.CreateCommand("item", h.Session, ["add", ticket.Id.ToString(), "1"])!.Execute();
            NotifyWeaponFashionInfo push = ReadPushPayload<NotifyWeaponFashionInfo>(h, nameof(NotifyWeaponFashionInfo), $"item add {ticket.Id}");
            AssertEqual(ticket.SubTypeParams[0], push.WeaponFashionDataList.Single().Id, $"item add {ticket.Id} pushes the fashion id");
            AssertEqual(0, h.Session.inventory.Items.Count(item => item.Id == ticket.Id), $"item add {ticket.Id} leaves no ticket in the bag");
        }
        AssertEqual(string.Join(",", new[] { first, second }.Select(row => row.SubTypeParams[0]).Order()),
            string.Join(",", h.Session.character.WeaponFashions.Select(row => row.Id).Order()), "Owned fashion ids are WeaponFashion ids");

        int packetId = 99_870;
        foreach (ItemTable ticket in new[] { first, second })
        {
            int fashionId = ticket.SubTypeParams[0];
            InvokeRegisteredRequestHandler("WeaponFashionUseRequest", h.Session, ++packetId, new WeaponFashionUseRequest { Id = fashionId, CharacterId = characterId });
            AssertEqual(0, ReadResponsePayload<WeaponFashionUseResponse>(h, packetId, nameof(WeaponFashionUseResponse), "WeaponFashionUse", 4).Code, $"Wear {fashionId}");
            AssertEqual(fashionId, h.Session.character.WeaponFashions.Single(row => row.UseCharacterList.Contains((int)characterId)).Id, $"Only {fashionId} is worn");
            // The ticket id is not a fashion id: wearing it is rejected.
            InvokeRegisteredRequestHandler("WeaponFashionUseRequest", h.Session, ++packetId, new WeaponFashionUseRequest { Id = ticket.Id, CharacterId = characterId });
            AssertEqual(true, ReadResponsePayload<WeaponFashionUseResponse>(h, packetId, nameof(WeaponFashionUseResponse), "WeaponFashionUse ticket id", 4).Code != 0, $"Ticket id {ticket.Id} is not wearable");
        }

        Type module = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.AccountModule");
        NotifyLogin login = (NotifyLogin)(RequiredMethod(module, "BuildNotifyLogin", BindingFlags.Static | BindingFlags.NonPublic, [typeof(Session)]).Invoke(null, [h.Session])
            ?? throw new InvalidDataException("AccountModule.BuildNotifyLogin returned nil."));
        AssertEqual(string.Join(",", new[] { first, second }.Select(row => row.SubTypeParams[0]).Order()),
            string.Join(",", login.WeaponFashionList.Select(row => row.Id).Order()), "NotifyLogin sends fashion ids");
        AssertEqual(second.SubTypeParams[0], login.WeaponFashionList.Single(row => row.UseCharacterList.Contains((int)characterId)).Id, "NotifyLogin keeps the worn fashion id");
        AssertEqual(0, login.ItemList.Count(item => item.Id == first.Id || item.Id == second.Id), "NotifyLogin carries no coating tickets");
    }
}
