using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.equip;
using AscNet.Table.V2.share.task;
using MessagePack;
using System.Reflection;

namespace AscNet.Test;

internal static partial class Program
{
    private static void ValidateVersion47MemoryCompatibility()
    {
        List<EquipTable> equips = TableReaderV2.Parse<EquipTable>();
        EquipConfigTable discount = TableReaderV2.Parse<EquipConfigTable>().Single();
        EquipTable[] santiago = equips
            .Where(row => row.Type == 0 && row.SuitId == discount.SuitId)
            .OrderBy(row => row.Site)
            .ToArray();
        AssertEqual(6, santiago.Length, "4.7 Santiago memory piece count");
        AssertIntegerList([1L, 2L, 3L, 4L, 5L, 6L], santiago.Select(row => (long)row.Site).ToArray(),
            "4.7 Santiago memory sites");

        HashSet<int> awakeIds = TableReaderV2.Parse<EquipAwakeTable>().Select(row => row.Id).ToHashSet();
        HashSet<int> resonanceIds = TableReaderV2.Parse<EquipResonanceTable>().Select(row => row.Id).ToHashSet();
        HashSet<int> materialIds = TableReaderV2.Parse<EquipResonanceUseItemTable>().Select(row => row.Id).ToHashSet();
        foreach (EquipTable memory in santiago)
        {
            AssertEqual(true, awakeIds.Contains(memory.Id), $"Santiago {memory.Id} awake recipe");
            AssertEqual(true, resonanceIds.Contains(memory.Id), $"Santiago {memory.Id} resonance pools");
            AssertEqual(true, materialIds.Contains(memory.Id), $"Santiago {memory.Id} resonance materials");
            AssertEqual(5, TableReaderV2.Parse<EquipBreakThroughTable>().Count(row => row.EquipId == memory.Id),
                $"Santiago {memory.Id} breakthrough stages");
        }

        Type module = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.EquipModule");
        MethodInfo resolveCost = RequiredMethod(module, "ResolveEquipResonanceCost",
            BindingFlags.Static | BindingFlags.NonPublic,
            [typeof(EquipTable), typeof(EquipResonanceUseItemTable), typeof(int)]);
        int Cost(EquipTable equip) => (int)(resolveCost.Invoke(null,
            [equip, TableReaderV2.Parse<EquipResonanceUseItemTable>().Single(row => row.Id == equip.Id), discount.ItemId]) ?? 0);

        AssertEqual(discount.DiscountCount, Cost(santiago[0]), "Santiago grid 1 discounted resonance cost");
        AssertEqual(discount.DiscountCount, Cost(santiago[5]), "Santiago grid 6 discounted resonance cost");
        EquipTable ordinary = equips.First(row => row.Type == 0 && row.Quality == 6 && row.SuitId != discount.SuitId
            && TableReaderV2.Parse<EquipResonanceUseItemTable>().Any(cost => cost.Id == row.Id
                && cost.ItemId.Contains(discount.ItemId)));
        EquipResonanceUseItemTable ordinaryCost = TableReaderV2.Parse<EquipResonanceUseItemTable>()
            .Single(row => row.Id == ordinary.Id);
        AssertEqual(ordinaryCost.ItemCount[ordinaryCost.ItemId.IndexOf(discount.ItemId)], Cost(ordinary),
            "ordinary memory retains configured resonance cost");
        AssertEqual(true, Cost(ordinary) > discount.DiscountCount,
            "Santiago discount is lower than ordinary memory cost");

        AssertResonance(santiago[0], discount.DiscountCount, expectedCode: 0, expectedRemaining: 0, "Santiago exact discount");
        AssertResonance(santiago[1], discount.DiscountCount - 1, expectedCode: 20012004,
            expectedRemaining: discount.DiscountCount - 1, "Santiago insufficient discount");

        void AssertResonance(
            EquipTable memory,
            long materialCount,
            int expectedCode,
            long expectedRemaining,
            string name)
        {
            const int characterId = 1071005;
            using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
                out RecordingMongoCollectionProxy<Player> players,
                out RecordingMongoCollectionProxy<Character> characters,
                out RecordingMongoCollectionProxy<Inventory> inventories);
            EquipData equip = new()
            {
                Id = checked((uint)(memory.Id + 10_000_000)),
                TemplateId = checked((uint)memory.Id)
            };
            AscNet.Common.Database.Character character = new()
            {
                Uid = equip.Id,
                Characters = [new CharacterData { Id = characterId }],
                Equips = [equip],
                Fashions = []
            };
            AscNet.Common.Database.Inventory inventory = new()
            {
                Uid = character.Uid,
                Items = [new Item { Id = discount.ItemId, Count = materialCount }]
            };
            using LoopbackSessionHarness harness = new(character, inventory: inventory);
            InvokeRequestHandler(harness, nameof(EquipResonanceRequest), checked((int)equip.Id),
                new EquipResonanceRequest
                {
                    UseItemId = discount.ItemId,
                    EquipId = checked((int)equip.Id),
                    CharacterId = characterId,
                    Slots = [1],
                    SelectSkillIds = null
                });
            if (expectedCode == 0)
            {
                NotifyItemDataList push = ReadPushPayload<NotifyItemDataList>(harness, nameof(NotifyItemDataList), $"{name} item push");
                AssertEqual(expectedRemaining, push.ItemDataList.Single(item => item.Id == discount.ItemId).Count,
                    $"{name} deducted material");
            }
            Packet packet = harness.ReadPacket($"{name} response or task progression");
            while (packet.Type == Packet.ContentType.Push)
            {
                AssertEqual(0, expectedCode, $"{name} rejected resonance emits no task push");
                Packet.Push push = MessagePackSerializer.Deserialize<Packet.Push>(packet.Content);
                AssertEqual(nameof(NotifyTask), push.Name, $"{name} only task progression precedes response");
                NotifyTask notification = MessagePackSerializer.Deserialize<NotifyTask>(push.Content);
                foreach (var task in notification.Tasks.Tasks)
                {
                    var schedule = task.Schedule.Single();
                    TaskTable? legacy = TableReaderV2.Parse<TaskTable>().Find(row => row.Id == task.Id);
                    CurrentTaskTable? current = TableReaderV2.Parse<CurrentTaskTable>().Find(row => row.Id == task.Id);
                    int conditionId = legacy?.Condition ?? current!.Condition;
                    AssertEqual((uint)conditionId, schedule.Id, $"{name} task uses configured condition");
                    ConditionTable? legacyCondition = legacy is null ? null
                        : TableReaderV2.Parse<ConditionTable>().Single(row => row.Id == conditionId);
                    CurrentConditionTable? currentCondition = legacy is not null ? null
                        : TableReaderV2.Parse<CurrentConditionTable>().Single(row => row.Id == conditionId);
                    int? type = legacyCondition?.Type ?? currentCondition!.Type;
                    var parameters = legacyCondition?.Params ?? currentCondition!.Params;
                    int amount;
                    if (type == 11202)
                    {
                        AssertEqual(true, parameters.Count <= 2
                            && (parameters.Count < 2 || parameters[1] == discount.ItemId),
                            $"{name} task counts only the spent material");
                        amount = checked((int)(materialCount - expectedRemaining));
                    }
                    else
                    {
                        AssertEqual(12205, type ?? 0, $"{name} task counts resonance");
                        AssertEqual(true, (parameters.Count < 2 || parameters[1] <= 0 || parameters[1] == memory.Id)
                            && (parameters.Count < 3 || parameters[2] < 0 || parameters[2] == 0 && memory.Site is >= 1 and <= 6)
                            && (parameters.Count < 4 || parameters[3] < 0 || parameters[3] == 0 && memory.Site == 0),
                            $"{name} resonance matches task equipment filter");
                        amount = 1;
                    }
                    AssertEqual(amount, harness.Session.player.MissionProgress.ConditionCounters[conditionId],
                        $"{name} committed condition progress");
                    AssertEqual(amount, players.LastReplacement!.MissionProgress.ConditionCounters[conditionId],
                        $"{name} persisted condition progress");
                    int target = legacy is not null ? legacy.Result ?? 1 : current!.Result;
                    AssertEqual(Math.Min(amount, target), schedule.Value, $"{name} table-derived task schedule");
                }
                packet = harness.ReadPacket($"{name} response or task progression");
            }
            EquipResonanceResponse response = ReadResponsePayload<EquipResonanceResponse>(
                packet, nameof(EquipResonanceResponse));
            AssertEqual(expectedCode, response.Code, $"{name} response Code");
            AssertEqual(expectedRemaining, inventory.Items.Single(item => item.Id == discount.ItemId).Count,
                $"{name} inventory material");
            AssertEqual(expectedCode == 0 ? 1 : 0, equip.ResonanceInfo.Count, $"{name} committed resonance count");
            AssertEqual(0, harness.Session.PendingEquipResonances.Count, $"{name} no provisional resonance");
            if (expectedCode == 0)
            {
                AssertEqual(response.ResonanceDatas.Single().TemplateId,
                    characters.LastReplacement!.Equips.Single().ResonanceInfo.Single().TemplateId,
                    $"{name} returned resonance is persisted");
                AssertEqual(expectedRemaining, inventories.LastReplacement!.Items.Single(item => item.Id == discount.ItemId).Count,
                    $"{name} persisted material balance");
            }
            else
            {
                AssertEqual(0, players.ReplaceOneCalls, $"{name} no player save");
                AssertEqual(0, characters.ReplaceOneCalls, $"{name} no character save");
                AssertEqual(0, inventories.ReplaceOneCalls, $"{name} no inventory save");
                AssertEqual(0, harness.Session.player.MissionProgress.ConditionCounters.Count,
                    $"{name} no task progression");
            }
            AssertEqual(false, harness.TryReadAvailablePacket($"{name} unexpected trailing packet", out _),
                $"{name} response completes sequence");
        }
    }
}
