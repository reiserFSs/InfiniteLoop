using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.client.draw;
using AscNet.Table.V2.share.character;
using AscNet.Table.V2.share.equip;
using AscNet.Table.V2.share.draw;
using AscNet.Table.V2.share.item;
using AscNet.Table.V2.share.partner;
using MessagePack;

namespace AscNet.Test;

internal partial class Program
{
   private static void ValidateVersion47DrawCubCompatibility()
    {
       AssertVersion47CurrentDrawsAvailable();
        AssertVersion47BannerCatalogRenders();
        AssertVersion47NoStalePools();
        AssertPatrickAndGrandDukeComposeWithoutSpecialCase();
        AssertCubDrawAcquisitionCreatesDistinctInstances();
        AssertPartnerMutationDurabilityAndCosts();
    }

   /// <summary>Regression: the 4.7 banner UI must not stall on Loading. The client renders the featured
    /// draw banner from the DrawGetDrawGroupList/DrawGetDrawInfoList flow; a group is only renderable
    /// when its Tag maps to a known DrawTabs entry (the featured Current Season banner is DrawTabs Id=2)
    /// and its group resolves at least one complete active DrawInfo. This exercises the real client
    /// handlers, asserting every advertised 4.7 banner (Themed 11/1509, Fate 15/2503, weapon 4/382,
    /// CUB 22/7069) is present, active, tagged for a renderable tab, and backed by a complete DrawInfo.</summary>
    private static void AssertVersion47BannerCatalogRenders()
    {
        AscNet.Common.Database.Character character = new()
        {
            Uid = 90_300,
            Characters = [],
            Equips = [],
            Fashions = [],
            Partners = []
        };
        using MongoCollectionOverride mongoOverride =
            MongoCollectionOverride.InstallForDailySignInCompatibility(out _, out _, out _);
        using LoopbackSessionHarness harness = new(character, sessionId: "banner-render-test");

        InvokeRequestHandler(harness, nameof(DrawGetDrawGroupListRequest), 20_000, new DrawGetDrawGroupListRequest());
        DrawGetDrawGroupListResponse groupRsp = ReadResponsePayload<DrawGetDrawGroupListResponse>(
            harness.ReadPacket("group list"), nameof(DrawGetDrawGroupListResponse));
        AssertEqual(0, groupRsp.Code, "banner group list code");

        // Every advertised 4.7 banner must be present as an active group with a renderable tab tag.
        Dictionary<int, DrawGroupInfo> groupsById = groupRsp.DrawGroupInfoList.ToDictionary(g => g.Id);
        foreach (int groupId in new[] { 11, 15, 4, 22 })
            AssertEqual(true, groupsById.ContainsKey(groupId), $"banner group {groupId} advertised at current clock");

        // Tag drives which DrawTabs entry renders the banner. The featured Themed/Fate construct banner
        // is the "Current Season" tab (DrawTabs Id=2); routing it to another tab (e.g. Crucible Id=9)
        // leaves the featured banner slot empty and the UI on Loading.
        AssertEqual(2, groupsById[11].Tag, "Themed featured banner tab tag");
        AssertEqual(2, groupsById[15].Tag, "Fate featured banner tab tag");
        AssertEqual(1, groupsById[4].Tag, "weapon banner tab tag");
        AssertEqual(7, groupsById[22].Tag, "CUB banner tab tag");

        // Each advertised group must resolve at least one complete, active DrawInfo the client renders.
        foreach ((int groupId, int drawId) in new[] { (11, 1509), (15, 2503), (4, 382), (22, 7069) })
        {
            InvokeRequestHandler(harness, nameof(DrawGetDrawInfoListRequest), 20_001, new DrawGetDrawInfoListRequest { GroupId = groupId });
            DrawGetDrawInfoListResponse infoRsp = ReadResponsePayload<DrawGetDrawInfoListResponse>(
                harness.ReadPacket($"info group {groupId}"), nameof(DrawGetDrawInfoListResponse));
            DrawInfo draw = infoRsp.DrawInfoList.Single(d => d.Id == drawId);
            AssertEqual(groupId, draw.GroupId, $"draw {drawId} group");
            AssertEqual(true, draw.Banner.Length > 0, $"draw {drawId} banner prefab");
            AssertEqual(true, draw.UseItemId > 0 && draw.UseItemCount > 0, $"draw {drawId} cost");
            AssertEqual(true, draw.ResourceIds.ContainsKey(1) && draw.ResourceIds[1] > 0, $"draw {drawId} target resource");
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            AssertEqual(true, draw.StartTime <= now && (draw.EndTime == 0 || now < draw.EndTime),
                $"draw {drawId} active at current clock");
        }
    }

    private static DrawInfo[] Version47CatalogTemplates()
    {
        return RequiredAscNetGameServerType("AscNet.GameServer.Game.DrawManager")
            .GetField("DrawTemplates", BindingFlags.Static | BindingFlags.NonPublic)?
            .GetValue(null) as DrawInfo[]
            ?? throw new MissingFieldException("AscNet.GameServer.Game.DrawManager", "DrawTemplates");
    }

    /// <summary>Assert the 4.7 featured/weapon/CUB draws (11/1509, 15/2503, 4/382, 22/7069) are exposed
    /// from the derived DrawServerCatalog table with the authoritative group/target/window/guarantee,
    /// and are active exactly within the official article-5308 window (unavailable outside it). Every
    /// asserted field is re-derived from the installed client tables and the official notice; a captured
    /// payload is only an oracle comparison, never a runtime source.</summary>
    private static void AssertVersion47CurrentDrawsAvailable()
    {
        List<DrawServerCatalogTable> catalog = TableReaderV2.Parse<DrawServerCatalogTable>().ToList();
        AssertEqual(4, catalog.Count, "Version47DrawCub derived draw catalog row count");
        var expected = new (int Id, int GroupId, string Category, int TargetId)[]
        {
            (1509, 11, "Themed", 1071005),
            (2503, 15, "Fate", 1071005),
            (382, 4, "Weapon", 2676001),
            (7069, 22, "Cub", 16410000),
        };
        foreach ((int drawId, int groupId, string category, int targetId) in expected)
        {
            DrawServerCatalogTable row = catalog.Single(r => r.Id == drawId);
            AssertEqual(groupId, row.GroupId, $"Version47DrawCub draw {drawId} group");
            AssertEqual(category, row.Category, $"Version47DrawCub draw {drawId} category");
            AssertEqual(targetId, row.TargetId, $"Version47DrawCub draw {drawId} target");
            AssertEqual(1787115600L, (long)row.StartTime, $"Version47DrawCub draw {drawId} official start");
            AssertEqual(1790204400L, (long)row.EndTime, $"Version47DrawCub draw {drawId} official end");
            AssertEqual(true, row.MaxBottomTimes > 0, $"Version47DrawCub draw {drawId} guarantee bound");
        }

        // The runtime catalog must contain exactly these four draws with matching group/target/window.
        DrawInfo[] templates = Version47CatalogTemplates();
        foreach ((int drawId, int groupId, string category, int targetId) in expected)
        {
            DrawInfo draw = templates.Single(d => d.Id == drawId);
            AssertEqual(groupId, draw.GroupId, $"Version47DrawCub runtime draw {drawId} group");
            AssertEqual(targetId, draw.ResourceIds.GetValueOrDefault(1), $"Version47DrawCub runtime draw {drawId} target");
            DrawServerCatalogTable row = catalog.Single(r => r.Id == drawId);
            AssertEqual((long)row.StartTime, draw.StartTime, $"Version47DrawCub runtime draw {drawId} start");
            AssertEqual((long)row.EndTime, draw.EndTime, $"Version47DrawCub runtime draw {drawId} end");
            AssertEqual(row.MaxBottomTimes, draw.MaxBottomTimes, $"Version47DrawCub runtime draw {drawId} guarantee");
        }

        // Availability is clock-gated by the official [start, end) window: active at the official
        // active clock (2026-08-24 sits inside the article-5308 window) and inactive outside it.
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        AssertEqual(true, now >= 1787115600L && now < 1790204400L,
            "Version47DrawCub test clock is inside the official 4.7 draw window");
        foreach (DrawServerCatalogTable row in catalog)
        {
            AssertEqual(true, row.StartTime <= now && now < row.EndTime,
                $"Version47DrawCub draw {row.Id} active at the official active clock");
        }

        var groups = (Array)RequiredAscNetGameServerType("AscNet.GameServer.Game.DrawManager")
            .GetField("GroupTemplates", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        foreach (DrawGroupInfo group in groups)
        {
            if (group.Id is 11 or 15)
            {
                AssertEqual(true, group.StartTime == 1787115600L && group.EndTime == 1790204400L,
                    $"Version47DrawCub derived group {group.Id} official window");
                AssertEqual(true, group.MaxBottomTimes > 0, $"Version47DrawCub derived group {group.Id} guarantee");
            }
        }

        // The named targets remain present in the authoritative tables.
        AssertEqual(true, TableReaderV2.Parse<PartnerTable>().Any(p => p.Id == 16_410_000),
            "Version47DrawCub Patrick 16410000 present in Partner table");
        AssertEqual(true, TableReaderV2.Parse<PartnerTable>().Any(p => p.Id == 16_400_000),
            "Version47DrawCub Grand Duke 16400000 present in Partner table");
        AssertEqual(true, TableReaderV2.Parse<ItemTable>().Any(i => i.Id == 241 && i.ItemType == 8),
            "Version47DrawCub Patrick shard 241 present as ItemType 8");
    }

    /// <summary>Stale 4.6 event groups (35/36) must not be advertised as active pools.</summary>
    private static void AssertVersion47NoStalePools()
    {
        var groups = (Array)RequiredAscNetGameServerType("AscNet.GameServer.Game.DrawManager")
            .GetField("GroupTemplates", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        foreach (DrawGroupInfo group in groups)
        {
            if (group.Id is 35 or 36
                && group.EndTime != 0
                && DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= group.EndTime)
            {
                continue;
            }
            if (group.Id is 35 or 36)
                throw new InvalidDataException(
                    $"Version47DrawCub stale event group {group.Id} must remain outside its window.");
        }
    }

    /// <summary>Patrick (16410000) and Grand Duke (16400000) compose through the generic table-derived
    /// constructor; no partner-specific branch exists.</summary>
    private static void AssertPatrickAndGrandDukeComposeWithoutSpecialCase()
    {
        foreach (int templateId in new[] { 16_410_000, 16_400_000 })
        {
            PartnerTable config = TableReaderV2.Parse<PartnerTable>().Single(p => p.Id == templateId);
            ItemTable shard = TableReaderV2.Parse<ItemTable>().Single(i => i.Id == config.ChipItemId);
            AscNet.Common.Database.Character character = new()
            {
                Uid = 90_001 + templateId,
                Characters = [],
                Equips = [],
                Fashions = [],
                Partners = []
            };
            AscNet.Common.Database.Inventory inventory = new()
            {
                Uid = character.Uid,
                Items = [new Item { Id = config.ChipItemId, Count = config.ChipNeedCount }]
            };
            using MongoCollectionOverride mongoOverride =
                MongoCollectionOverride.InstallForDailySignInCompatibility(out _, out _, out _);
            using LoopbackSessionHarness harness = new(character, inventory: inventory, sessionId: $"partner-{templateId}-test");

            InvokeRequestHandler(harness, nameof(PartnerComposeRequest), 19_001,
                new PartnerComposeRequest { TemplateIds = [templateId], IsOneKey = false });
            AssertItemPush(harness.ReadPacket("PartnerComposeRequest item push"), config.ChipItemId, 0,
                "PartnerComposeRequest item push");
            Packet partnerPacket = harness.ReadPacket("NotifyPartnerDataList");
            AssertEqual(Packet.ContentType.Push, partnerPacket.Type, "NotifyPartnerDataList packet type");
            Packet.Push partnerPush = MessagePackSerializer.Deserialize<Packet.Push>(partnerPacket.Content);
            NotifyPartnerDataList payload = MessagePackSerializer.Deserialize<NotifyPartnerDataList>(partnerPush.Content);
            PartnerData partner = payload.PartnerDataList.Single();
            AssertEqual(templateId, partner.TemplateId, $"composed partner {templateId} template");
            AssertIntegerList([1], payload.OperateTypes.Select(v => (long)v).ToArray(), "compose operation");
            AssertEqual(config.InitQuality, partner.Quality, $"composed partner {templateId} initial quality");
            AssertEqual(1, partner.Level, $"composed partner {templateId} level");
            AssertEqual(1, character.Partners.Count, $"composed partner {templateId} persisted count");
            AssertEqual(0, ((PartnerComposeResponse)ReadResponsePayload(
                harness, 19_001, nameof(PartnerComposeResponse), "PartnerComposeResponse",
                typeof(PartnerComposeResponse), maxPacketsToRead: 16)).Code,
                $"composed partner {templateId} code");
            AssertEqual(true, harness.Session.player.ArchivePartnerUnlockIds.Contains(templateId),
                $"composed partner {templateId} archive membership");
        }
    }

    /// <summary>Repeated CUB draw acquisition must create distinct fuseable PartnerData instances, and
    /// the NotifyPartnerDataList Obtain push must precede the draw response.</summary>
    private static void AssertCubDrawAcquisitionCreatesDistinctInstances()
    {
        DrawServerCatalogTable currentCub = TableReaderV2.Parse<DrawServerCatalogTable>()
            .Single(row => row.Category.Equals("Cub", StringComparison.Ordinal));
        DrawInfo cubDraw = Version47CatalogTemplates()
            .Single(draw => draw.Id == currentCub.Id);
        int partnerTemplate = currentCub.TargetId;

        AscNet.Common.Database.Character character = new()
        {
            Uid = 90_100,
            Characters = [],
            Equips = [],
            Fashions = [],
            Partners = []
        };
        AscNet.Common.Database.Inventory inventory = new()
        {
            Uid = character.Uid,
            Items = [new Item { Id = cubDraw.UseItemId, Count = 5000 }]
        };
        using MongoCollectionOverride mongoOverride =
            MongoCollectionOverride.InstallForDailySignInCompatibility(out _, out _, out _);
        using LoopbackSessionHarness harness = new(character, inventory: inventory, sessionId: "cub-draw-test");

        int packetId = 19_100;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            int before = character.Partners.Count;
            InvokeRegisteredRequestHandler(nameof(DrawDrawCardRequest), harness.Session, packetId,
                new DrawDrawCardRequest { DrawId = cubDraw.Id, Count = 1, UseDrawTicketId = 0 });
            Packet pushPacket = harness.ReadPacket($"draw attempt {attempt} first packet");
            AssertEqual(Packet.ContentType.Push, pushPacket.Type, $"draw attempt {attempt} push before response");
            Packet.Push push = MessagePackSerializer.Deserialize<Packet.Push>(pushPacket.Content);
            if (push.Name == nameof(NotifyItemDataList))
            {
                pushPacket = harness.ReadPacket($"draw attempt {attempt} partner push");
                AssertEqual(Packet.ContentType.Push, pushPacket.Type, $"draw attempt {attempt} partner push");
                push = MessagePackSerializer.Deserialize<Packet.Push>(pushPacket.Content);
            }
            AssertEqual(nameof(NotifyPartnerDataList), push.Name, $"draw attempt {attempt} partner push name");
            NotifyPartnerDataList partnerPush =
                MessagePackSerializer.Deserialize<NotifyPartnerDataList>(push.Content);
            PartnerData acquired = partnerPush.PartnerDataList.Single();
            AssertEqual(partnerTemplate, acquired.TemplateId, $"draw attempt {attempt} template");
            AssertIntegerList([1], partnerPush.OperateTypes.Select(v => (long)v).ToArray(),
                $"draw attempt {attempt} obtain operation");

            DrawDrawCardResponse rsp = (DrawDrawCardResponse)ReadResponsePayload(
                harness, packetId++, nameof(DrawDrawCardResponse), $"draw attempt {attempt} response",
                typeof(DrawDrawCardResponse), maxPacketsToRead: 16);
            AssertEqual(0, rsp.Code, $"draw attempt {attempt} code");
            AssertEqual(before + 1, character.Partners.Count, $"draw attempt {attempt} persisted partner count");
            AssertEqual(true, harness.Session.player.ArchivePartnerUnlockIds.Contains(acquired.TemplateId),
                $"draw attempt {attempt} archive membership");
        }

        if (character.Partners.Select(p => p.Id).Distinct().Count() != 2)
            throw new InvalidDataException("Version47DrawCub repeated draws must create distinct partner ids.");
    }

    /// <summary>Mutation handlers must persist on success, reject insufficient resources, and apply
    /// table-derived costs — no successful mutation is memory-only.</summary>
    private static void AssertPartnerMutationDurabilityAndCosts()
    {
        int templateId = 16_410_000;
        PartnerTable config = TableReaderV2.Parse<PartnerTable>().Single(p => p.Id == templateId);

        AscNet.Common.Database.Character character = new()
        {
            Uid = 90_200,
            Characters = [],
            Equips = [],
            Fashions = [],
            Partners =
            [
                new PartnerData
                {
                    Id = 1,
                    TemplateId = templateId,
                    Level = 1,
                    Quality = config.InitQuality,
                    SkillList = BuildInitialSkillListFor(templateId),
                    UnlockSkillGroup = TableReaderV2.Parse<PartnerSkillTable>()
                        .Single(s => s.PartnerId == templateId).MainSkillGroupId.ToList()
                }
            ]
        };
        AscNet.Common.Database.Inventory inventory = new()
        {
            Uid = character.Uid,
            Items =
            [
                new Item { Id = AscNet.Common.Database.Inventory.Coin, Count = 1_000_000 },
                new Item { Id = 30113, Count = 100 }
            ]
        };

        using MongoCollectionOverride mongoOverride =
            MongoCollectionOverride.InstallForDailySignInCompatibility(
                out _,
                out RecordingMongoCollectionProxy<AscNet.Common.Database.Character> characterCollection,
                out _);
        using LoopbackSessionHarness harness = new(character, inventory: inventory, sessionId: "partner-mutation-test");

        PartnerData partner = character.Partners.Single();
        int savesBefore = characterCollection.ReplaceOneCalls;

        AscNet.Common.Database.Character poorCharacter = new()
        {
            Uid = 90_201,
            Characters = [],
            Equips = [],
            Fashions = [],
            Partners = character.Partners.Select(BsonClone).ToList()
        };
        AscNet.Common.Database.Inventory poorInventory = new()
        {
            Uid = 90_201,
            Items = [new Item { Id = AscNet.Common.Database.Inventory.Coin, Count = 0 }]
        };
        using LoopbackSessionHarness poorHarness = new(poorCharacter, inventory: poorInventory, sessionId: "partner-poor-test");
        InvokeRequestHandler(poorHarness, nameof(PartnerLevelUpRequest), 19_201,
            new PartnerLevelUpRequest { PartnerId = partner.Id, UseItems = new() { [30113] = 1 } });
        AssertEqual(1, ((PartnerLevelUpResponse)ReadResponsePayload(
            poorHarness, 19_201, nameof(PartnerLevelUpResponse), "insufficient PartnerLevelUpResponse",
            typeof(PartnerLevelUpResponse), maxPacketsToRead: 16)).Code,
            "insufficient partner level-up rejected");

        PartnerBreakThroughTable breakthrough = TableReaderV2.Parse<PartnerBreakThroughTable>()
            .Single(b => b.PartnerId == templateId && b.BreakTimes == 0);
        InvokeRequestHandler(harness, nameof(PartnerLevelUpRequest), 19_202,
            new PartnerLevelUpRequest { PartnerId = partner.Id, UseItems = new() { [30113] = 1 } });
        harness.ReadPacket("level-up item push");
        PartnerLevelUpResponse levelUp = (PartnerLevelUpResponse)ReadResponsePayload(
            harness, 19_202, nameof(PartnerLevelUpResponse), "PartnerLevelUpResponse",
            typeof(PartnerLevelUpResponse), maxPacketsToRead: 16);
        AssertEqual(0, levelUp.Code, "partner level-up code");
        AssertEqual(true, partner.Level > 1, "partner level advanced");
        AssertEqual(savesBefore + 1, characterCollection.ReplaceOneCalls, "partner level-up persists Character");
        AssertEqual(true, partner.Level <= breakthrough.LevelLimit, "partner level respects breakthrough cap");
    }

    private static List<PartnerSkillData> BuildInitialSkillListFor(int templateId)
    {
        PartnerSkillTable skillConfig = TableReaderV2.Parse<PartnerSkillTable>().Single(s => s.PartnerId == templateId);
        PartnerMainSkillGroupTable mainGroup = TableReaderV2.Parse<PartnerMainSkillGroupTable>()
            .Single(g => g.Id == skillConfig.DefaultMainSkillGroupId);
        ILookup<int, PartnerPassiveSkillGroupTable> passives =
            TableReaderV2.Parse<PartnerPassiveSkillGroupTable>().ToLookup(g => g.Id);
        List<PartnerSkillData> skills =
        [
            new PartnerSkillData { Id = mainGroup.SkillId.First(), Level = 1, IsWear = true, Type = 1 }
        ];
        skills.AddRange(skillConfig.PassiveSkillGroupId.Select(gid => new PartnerSkillData
        {
            Id = passives[gid].Single().SkillId,
            Level = 1,
            IsWear = false,
            Type = 2
        }));
        return skills;
    }

    private static PartnerData BsonClone(PartnerData source)
    {
        return new PartnerData
        {
            Id = source.Id,
            TemplateId = source.TemplateId,
            Name = source.Name,
            CharacterId = source.CharacterId,
            Level = source.Level,
            Exp = source.Exp,
            BreakThrough = source.BreakThrough,
            IsLock = source.IsLock,
            Quality = source.Quality,
            StarSchedule = source.StarSchedule,
            SkillList = source.SkillList.Select(s => new PartnerSkillData
            {
                Id = s.Id,
                Level = s.Level,
                IsWear = s.IsWear,
                Type = s.Type
            }).ToList(),
            UnlockSkillGroup = source.UnlockSkillGroup.ToList(),
            CreateTime = source.CreateTime
        };
    }
}
