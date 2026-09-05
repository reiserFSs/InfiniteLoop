using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.draw;
using AscNet.Table.V2.share.task;

namespace AscNet.Test;

internal partial class Program
{
    private static void ValidateDrawTaskProgressCompatibility()
    {
        DrawServerCatalogTable catalog = TableReaderV2.Parse<DrawServerCatalogTable>()
            .Single(row => row.Category == "Cub");
        DrawInfo draw = Version47CatalogTemplates().Single(row => row.Id == catalog.Id);
        const int count = 10;
        int debit = checked(draw.UseItemCount * count);
        Character character = new() { Uid = 90_110, Characters = [], Equips = [], Fashions = [], Partners = [] };
        Inventory inventory = new()
        {
            Uid = character.Uid,
            Items = [new Item { Id = draw.UseItemId, Count = debit }]
        };
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(out _, out _, out _);
        using LoopbackSessionHarness harness = new(character, inventory: inventory, sessionId: "draw-task-progress");
        harness.Session.stage = CreateLoginAccountCompatibilityStage(character.Uid);
        int[] otherCurrencyConditions = TableReaderV2.Parse<CurrentConditionTable>()
            .Where(row => row.Type == 11202 && row.Params.Count > 1 && row.Params[1] != draw.UseItemId)
            .Select(row => row.Id).ToArray();
        Dictionary<int, int> before = otherCurrencyConditions.ToDictionary(id => id,
            id => harness.Session.player.MissionProgress.ConditionCounters.GetValueOrDefault(id));

        InvokeRegisteredRequestHandler(nameof(DrawDrawCardRequest), harness.Session, 19_120,
            new DrawDrawCardRequest { DrawId = draw.Id, Count = count });
        DrawDrawCardResponse success = (DrawDrawCardResponse)ReadResponsePayload(harness, 19_120,
            nameof(DrawDrawCardResponse), "bulk draw spending", typeof(DrawDrawCardResponse), maxPacketsToRead: 64);
        AssertEqual(0, success.Code, "bulk draw succeeds");
        AssertEqual(0L, inventory.Items.Single(item => item.Id == draw.UseItemId).Count, "bulk draw consumes exact ticket cost");
        AssertEqual(count, success.ClientDrawInfo!.TotalCount, "bulk draw commits all pulls");
        AssertEqual(count, character.Partners.Count, "bulk draw grants all CUBs");
        AssertEqual(true, character.Partners.All(partner => harness.Session.player.ArchivePartnerUnlockIds.Contains(partner.TemplateId)),
            "acquired CUBs unlock archive membership");
        foreach (int id in otherCurrencyConditions)
            AssertEqual(before[id], harness.Session.player.MissionProgress.ConditionCounters.GetValueOrDefault(id),
                $"draw tickets do not count as another currency for condition {id}");

        InvokeRegisteredRequestHandler(nameof(DrawDrawCardRequest), harness.Session, 19_121,
            new DrawDrawCardRequest { DrawId = draw.Id, Count = count });
        DrawDrawCardResponse failure = (DrawDrawCardResponse)ReadResponsePayload(harness, 19_121,
            nameof(DrawDrawCardResponse), "unaffordable draw", typeof(DrawDrawCardResponse), maxPacketsToRead: 64);
        if (failure.Code == 0)
            throw new InvalidDataException("Unaffordable draw succeeded.");
        AssertEqual(count, character.Partners.Count, "rejected draw grants no CUBs");
        AssertEqual(count, harness.Session.player.DrawState.ProgressByDrawId[draw.Id].TotalCount,
            "rejected draw adds no pulls");
        foreach (int id in otherCurrencyConditions)
            AssertEqual(before[id], harness.Session.player.MissionProgress.ConditionCounters.GetValueOrDefault(id),
                $"rejected draw does not advance spending condition {id}");
    }
}
