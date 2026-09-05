using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.archive;
using AscNet.Table.V2.share.condition;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using System.Reflection;

namespace AscNet.Test;

internal partial class Program
{
    private static void ValidatePartnerStoryCompatibility()
    {
        PacketFactory.LoadPacketHandlers();
        Type module = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.PartnerModule");
        MethodInfo refresh = module.GetMethod("RefreshArchive", BindingFlags.NonPublic | BindingFlags.Static)!;
        MethodInfo sync = module.GetMethod("SyncArchive", BindingFlags.NonPublic | BindingFlags.Static)!;
        var settings = TableReaderV2.Parse<PartnerSettingTable>();
        var conditions = TableReaderV2.Parse<ConditionTable>().ToDictionary(row => row.Id);
        foreach (var setting in settings.Where(row => row.Condition != 0))
            AssertEqual(true, conditions.TryGetValue(setting.Condition, out var condition)
                && condition.Type is 10136 or 10137 or 10138, "Every partner setting has supported authority");

        const long uid = 49_701;
        Player player = CreateDrawCompatibilityPlayer(uid);
        Character character = CreateDrawCompatibilityCharacter(uid);
        character.Partners = [];
        bool Refresh() => (bool)refresh.Invoke(null, [player, character])!;
        AssertEqual(false, Refresh(), "No unowned partner stories unlock");
        int[] templates = settings.Select(row => row.GroupId).Distinct().Take(2).ToArray();
        foreach (int template in templates)
        {
            var partner = new PartnerData
            {
                Id = character.Partners.Count + 1, TemplateId = template, Level = 9, Quality = 1,
                SkillList = [new PartnerSkillData { Id = 1, Type = 1, Level = 1 },
                    new PartnerSkillData { Id = 2, Type = 2, Level = 1 }]
            };
            character.Partners.Add(partner);
            Refresh();
            var gated = settings.Where(row => row.GroupId == template && row.Condition != 0).ToArray();
            AssertEqual(false, gated.Any(row => player.ArchivePartnerSettings.Contains(row.Id)), "Unmet requirements stay locked");
            partner.Level = 10;
            Refresh();
            AssertEqual(true, player.ArchivePartnerSettings.Contains(gated.Single(row => conditions[row.Condition].Type == 10136).Id), "Level eligibility unlocks text automatically");
            AssertEqual(false, player.ArchivePartnerSettings.Contains(gated.Single(row => conditions[row.Condition].Type == 10137).Id), "Level does not bypass rank gate");
            partner.Quality = conditions[gated.Single(row => conditions[row.Condition].Type == 10137).Condition].Params[1];
            partner.SkillList[1].Level = 9;
            Refresh();
            AssertEqual(true, gated.All(row => player.ArchivePartnerSettings.Contains(row.Id)), "Rank and combined main/passive skill requirements unlock independently");
        }
        player = BsonSerializer.Deserialize<Player>(player.ToBson());
        character.Partners.Clear();
        AssertEqual(false, Refresh(), "Consumed partners retain durable archive unlocks after relog");
        AssertEqual(true, settings.Where(row => templates.Contains(row.GroupId)).All(row => player.ArchivePartnerSettings.Contains(row.Id)), "All earned story text remains readable after relog");

        // Exercise a registered request through the real session boundary.
        var levelSetting = settings.First(row => row.Condition != 0 && conditions[row.Condition].Type == 10136);
        player = CreateDrawCompatibilityPlayer(uid);
        character.Partners = [new PartnerData { Id = 1, TemplateId = levelSetting.GroupId, Level = 9, Quality = 1 }];
        Refresh();
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(out var playerCollection, out _, out _);
        using LoopbackSessionHarness harness = new(character, player,
            CreateDrawCompatibilityInventory(uid, [new Item { Id = Inventory.Coin, Count = 100_000 }, new Item { Id = 30113, Count = 1 }]), "partner-story-loopback");
        harness.Session.stage = CreateLoginAccountCompatibilityStage(uid);
        typeof(Session).GetMethod("InvokeRequestHandler", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(harness.Session, [GetRegisteredRequestHandler(nameof(PartnerLevelUpRequest)), new Packet.Request
            {
                Name = nameof(PartnerLevelUpRequest), Id = 49_711,
                Content = MessagePack.MessagePackSerializer.Serialize(new PartnerLevelUpRequest
                {
                    PartnerId = 1, UseItems = new() { [30113] = 1 }
                })
            }]);
        _ = ReadPushPayload<NotifyItemDataList>(harness, nameof(NotifyItemDataList), "Partner story level material cost");
        var pushed = ReadPushPayload<NotifyPartnerSettings>(harness, nameof(NotifyPartnerSettings), "Partner story eligibility push");
        AssertEqual(true, pushed.PartnerSettings.Contains(levelSetting.Id), "Client receives newly unlocked setting ID");
        AssertEqual(0, ReadResponsePayload<PartnerLevelUpResponse>(harness, 49_711, nameof(PartnerLevelUpResponse), "Partner story level response").Code, "Level-up action succeeds");
        player = BsonSerializer.Deserialize<Player>(harness.Session.player.ToBson());
        AssertEqual(true, player.ArchivePartnerSettings.Contains(levelSetting.Id), "Pushed story unlock survives BSON persistence");
        sync.Invoke(null, [harness.Session]);
        AssertEqual(false, (bool)refresh.Invoke(null, [harness.Session.player, character])!, "Repeated sync does not re-award stories");

        var rankSetting = settings.Single(row => row.GroupId == levelSetting.GroupId
            && row.Condition != 0 && conditions[row.Condition].Type == 10137);
        character.Partners[0].Quality = conditions[rankSetting.Condition].Params[1];
        character.Save();
        bool failedSave = false;
        playerCollection.ThrowOnReplaceOne = true;
        try
        {
            sync.Invoke(null, [harness.Session]);
        }
        catch (TargetInvocationException)
        {
            failedSave = true;
        }
        finally
        {
            playerCollection.ThrowOnReplaceOne = false;
        }
        AssertEqual(true, failedSave, "Archive persistence failure is not reported as a successful unlock");
        AssertEqual(false, harness.Session.player.ArchivePartnerSettings.Contains(rankSetting.Id),
            "Failed archive save rolls back the in-memory unlock for retry");
        sync.Invoke(null, [harness.Session]);
        var retried = ReadPushPayload<NotifyPartnerSettings>(harness, nameof(NotifyPartnerSettings), "Retried archive save");
        AssertEqual(true, retried.PartnerSettings.Contains(rankSetting.Id), "Retry publishes the previously uncommitted story");
        AssertEqual(true, BsonSerializer.Deserialize<Player>(harness.Session.player.ToBson()).ArchivePartnerSettings.Contains(rankSetting.Id),
            "Retry retains the story in durable state");
    }
}
