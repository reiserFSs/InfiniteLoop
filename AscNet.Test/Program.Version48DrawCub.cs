using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.reward;
using AscNet.Table.V2.share.item;
using AscNet.Table.V2.share.task;
using MessagePack;
using MongoDB.Bson.Serialization;
using MongoDB.Bson;
using System.Reflection;

namespace AscNet.Test;

internal partial class Program
{
    private static void ValidateVersion48DrawCubCompatibility()
    {
        using MongoCollectionOverride noOpStages = MongoCollectionOverride.InstallNoOpStageCollection(); // login persists Stage rollover
        AssertPartnerComposeViaSharedConstructor([16_420_000, 16_430_000]);
        var templates = Version47CatalogTemplates().ToDictionary(draw => draw.Id);
        var manager = RequiredAscNetGameServerType("AscNet.GameServer.Game.DrawManager");
        var rewardReady = manager.GetMethod("HasRewardConfiguration", BindingFlags.Public | BindingFlags.Static)!;
        AssertEqual(false, (bool)rewardReady.Invoke(null, [5613])!, "collab Fate 5613 stays closed");
        foreach (int drawId in new[] { 5612, 381, 7068, 1516, 383, 7071 })
            AssertEqual(true, (bool)rewardReady.Invoke(null, [drawId])!, $"4.8 draw {drawId} is available");

        var lawful = manager.GetMethod("HasAvailablePityLaw", BindingFlags.NonPublic | BindingFlags.Static)!;
        AssertEqual(false, (bool)lawful.Invoke(null, [templates[5613]])!,
            "collab Fate remains unavailable without an 80–100 threshold law");
        AssertEqual(true, (bool)lawful.Invoke(null, [templates[5612]])!,
            "fixed-pity collab character has an available law");
        AssertPartnerAtomicRewardClaim();
        AssertFreeDrawTicketUse();
        AssertPaidDrawFrozenRecovery();
        AssertCollabPaidDraws();
        AssertCollabEarlyOpenWindow();
    }

    /// <summary>AscNet policy: collab groups open at the 4.8 maintenance end (1790226000), before
    /// retail 1790676000. Groups 37, 39, and 40 carry client DrawTabs Tag 5 (Collab). The client
    /// sorts a tab by Order descending, so weapon group 39 uses Order 2 between frame 4002 and CUB 1.
    /// Season weapon group 4 and CUB group 22 stay on tags 1 and 7. Fate 5613 stays closed.</summary>
    private static void AssertCollabEarlyOpenWindow()
    {
        var manager = RequiredAscNetGameServerType("AscNet.GameServer.Game.DrawManager");
        var clock = manager.GetField("UtcNow", BindingFlags.NonPublic | BindingFlags.Static)!;
        var priorClock = (Func<DateTimeOffset>)clock.GetValue(null)!;
        MethodInfo groupInfos = RequiredMethod(manager, "GetDrawGroupInfos", BindingFlags.Static | BindingFlags.Public, [typeof(Player)]);
        MethodInfo drawInfo = RequiredMethod(manager, "GetDrawInfoById", BindingFlags.Static | BindingFlags.Public, [typeof(int), typeof(Player)]);
        MethodInfo dalBuilder = RequiredMethod(RequiredAscNetGameServerType("AscNet.GameServer.Handlers.DrawModule"),
            "BuildNotifyDateALiveDraw", BindingFlags.Static | BindingFlags.NonPublic, []);
        Player player = CreateDrawCompatibilityPlayer(48_070);
        try
        {
            clock.SetValue(null, (Func<DateTimeOffset>)(() => DateTimeOffset.FromUnixTimeSeconds(1790226000 - 1)));
            AssertEqual(false, ((List<DrawGroupInfo>)groupInfos.Invoke(null, [player])!).Any(group => group.Id == 37),
                "Kurumi group 37 is not advertised before the 4.8 maintenance end");
            AssertEqual(true, drawInfo.Invoke(null, [5612, player]) is null, "5612 is not drawable before the window");
            AssertEqual(0, ((NotifyDateALiveDraw)dalBuilder.Invoke(null, [])!).OpenDraws.Count,
                "NotifyDateALiveDraw is empty before the window");

            clock.SetValue(null, (Func<DateTimeOffset>)(() => DateTimeOffset.FromUnixTimeSeconds(1790400000)));
            List<DrawGroupInfo> groups = ((List<DrawGroupInfo>)groupInfos.Invoke(null, [player])!);
            AssertEqual(5, groups.Single(group => group.Id == 37).Tag, "Kurumi group 37 opens early with DrawTabs Tag 5");
            AssertEqual(5, groups.Single(group => group.Id == 39).Tag, "Collab weapon group 39 uses the Collab tab");
            AssertEqual(5, groups.Single(group => group.Id == 40).Tag, "Collab CUB group 40 uses the Collab tab");
            AssertEqual(1, groups.Single(group => group.Id == 4).Tag, "Season weapon group 4 stays on the Weapon tab");
            AssertEqual(7, groups.Single(group => group.Id == 22).Tag, "Season CUB group 22 stays on the CUB tab");
            AssertEqual("37,39,40", string.Join(",", groups.Where(group => group.Tag == 5)
                .OrderByDescending(group => group.Order).ThenByDescending(group => group.Priority)
                .Select(group => group.Id)),
                "Collab tab Order descending is frame, weapon, then CUB");
            AssertEqual(4002, groups.Single(group => group.Id == 37).Order, "Collab frame Order stays 4002");
            AssertEqual(2, groups.Single(group => group.Id == 39).Order, "Collab weapon Order sits under the frame");
            AssertEqual(1, groups.Single(group => group.Id == 40).Order, "Collab CUB Order stays 1");
            AssertEqual(510, groups.Single(group => group.Id == 39).Priority, "Collab weapon priority stays 510");
            AssertEqual(8100, groups.Single(group => group.Id == 40).Priority, "Collab CUB priority stays 8100");
            AssertEqual(1, groups.Single(group => group.Id == 4).Order, "Season weapon Order stays 1");
            AssertEqual(1, groups.Single(group => group.Id == 22).Order, "Season CUB Order stays 1");
            AssertEqual(500, groups.Single(group => group.Id == 4).Priority, "Season weapon priority stays 500");
            AssertEqual(8000, groups.Single(group => group.Id == 22).Priority, "Season CUB priority stays 8000");
            AssertEqual(false, groups.Any(group => group.Id == 38), "collab Fate group 38 stays unadvertised");
            AssertEqual(true, drawInfo.Invoke(null, [5612, player]) is not null, "5612 is drawable before retail start");
            AssertEqual(true, drawInfo.Invoke(null, [5613, player]) is null, "5613 stays closed in window");
            Dictionary<int, List<int>> open = ((NotifyDateALiveDraw)dalBuilder.Invoke(null, [])!).OpenDraws;
            AssertEqual("1:5612", string.Join(";", open.Select(pair => $"{pair.Key}:{string.Join(",", pair.Value)}")),
                "NotifyDateALiveDraw lists only open DAL draws");
        }
        finally { clock.SetValue(null, priorClock); }
    }

    private static void AssertPartnerAtomicRewardClaim()
    {
        const int templateId = 16_430_000; // Kelpie, an authored 4.8 CUB.
        const string claimKey = "draw-cub-48:kelpie-claim";
        const long uid = 48_053;
        Type handler = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.RewardHandler");
        Type grantType = handler.Assembly.GetType("AscNet.GameServer.Handlers.RewardGrant", throwOnError: true)!;
        MethodInfo apply = handler.GetMethod("ApplyRewardsOnceAndPersist", BindingFlags.Static | BindingFlags.Public)
            ?? throw new MissingMethodException(handler.FullName, "ApplyRewardsOnceAndPersist");
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
            out _, out var characterSaves, out _);
        using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(uid),
            CreateDrawCompatibilityPlayer(uid), CreateDrawCompatibilityInventory(uid, []), "draw-cub-claim-48");
        RewardGoodsTable reward = new() { Id = templateId, TemplateId = templateId, Count = 1, Params = [] };
        void Apply()
        {
            Array grants = Array.CreateInstance(grantType, 1);
            grants.SetValue(Activator.CreateInstance(grantType, claimKey, new[] { reward }, null, null), 0);
            _ = apply.Invoke(null, [grants, harness.Session]);
        }
        Apply();
        PartnerData acquired = harness.Session.character.Partners.Single(partner => partner.TemplateId == templateId);
        AssertEqual(true, harness.Session.character.AppliedRewardClaims.Contains(claimKey),
            "new CUB draw grant persists a character receipt");
        Character persisted = BsonSerializer.Deserialize<Character>(characterSaves.LastSuccessfulReplacementBson
            ?? throw new InvalidDataException("New CUB draw grant did not persist."));
        AssertEqual(acquired.Id, persisted.Partners.Single().Id, "new CUB instance survives BSON reload");
        harness.Session.character = persisted;
        Apply();
        AssertEqual(acquired.Id, harness.Session.character.Partners.Single().Id,
            "replayed CUB claim retains one identical instance instead of granting twice");
    }

    private static void AssertFreeDrawTicketUse()
    {
        var manager = RequiredAscNetGameServerType("AscNet.GameServer.Game.DrawManager");
        var clock = manager.GetField("UtcNow", BindingFlags.NonPublic | BindingFlags.Static)!;
        var priorClock = (Func<DateTimeOffset>)clock.GetValue(null)!;
        var ticketManager = RequiredAscNetGameServerType("AscNet.GameServer.Game.DrawTicketManager");
        var grant = ticketManager.GetMethod("Grant", BindingFlags.NonPublic | BindingFlags.Static)!;
        var available = ticketManager.GetMethod("Available", BindingFlags.NonPublic | BindingFlags.Static)!;
        DateTimeOffset eventTime = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
        clock.SetValue(null, (Func<DateTimeOffset>)(() => eventTime));
        try
        {
            using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
                out var playerSaves, out var characterSaves, out var inventorySaves);
            long uid = 48_051;
            using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(uid),
                CreateDrawCompatibilityPlayer(uid), CreateDrawCompatibilityInventory(uid, []), "free-ticket-48");
            Player player = harness.Session.player;
            foreach (int cfgId in new[] { 21000901, 21000902, 21000903 })
                grant.Invoke(null, [player, cfgId, 1, eventTime]);
            player.SaveChecked();
            var free = player.DrawState.FreeTickets.ToDictionary(ticket => ticket.CfgId);
            AssertEqual(true, free[21000901].Id != free[21000902].Id,
                "free single and ten-draw ticket instances have distinct serial IDs");
            AssertEqual(true, available.Invoke(null, [player, free[21000901].Id, 37, 1, eventTime]) is not null,
                "authored single ticket belongs to Kurumi group");
            AssertEqual(true, available.Invoke(null, [player, free[21000901].Id, 37, 10, eventTime]) is null,
                "single ticket never funds ten draws");
            AssertEqual(true, available.Invoke(null, [player, free[21000902].Id, 37, 10, eventTime]) is not null,
                "authored ten ticket belongs to Kurumi group");
            AssertEqual(true, available.Invoke(null, [player, free[21000902].Id, 37, 1, eventTime]) is null,
                "ten ticket never funds one draw");
            AssertEqual(true, available.Invoke(null, [player, free[21000903].Id, 37, 1, eventTime]) is null,
                "weapon ticket does not belong to Kurumi group");
            AssertEqual(true, available.Invoke(null, [player, free[21000903].Id, 39, 1, eventTime]) is not null,
                "authored weapon ticket belongs to group39");
            // AscNet policy: collab Fate 5613 stays closed, even for a player who owns a matching group38 ticket.
            AssertEqual(1, DrawWithTicket(harness, 5613, 1, free[21000901].Id, 48_601, "collab Fate free draw").Code,
                "collab Fate rejects an owned collab ticket");
            Player stored = BsonSerializer.Deserialize<Player>(playerSaves.LastSuccessfulReplacementBson
                ?? throw new InvalidDataException("Free ticket stock did not persist."));
            AssertEqual(1, stored.DrawState.FreeTickets.Single(ticket => ticket.CfgId == 21000901).Count,
                "rejected collab Fate draw leaves earned single ticket persisted");
            AssertEqual(1, stored.DrawState.FreeTickets.Single(ticket => ticket.CfgId == 21000902).Count,
                "rejected collab Fate draw leaves earned ten ticket persisted");
            AssertEqual(true, available.Invoke(null, [stored, free[21000902].Id, 37, 10,
                new DateTimeOffset(2026, 11, 4, 0, 0, 0, TimeSpan.Zero)]) is null,
                "expired free ticket cannot fund a draw");
            AssertEqual(1, DrawWithTicket(harness, 381, 1, free[21000901].Id, 48_607, "character ticket on weapon").Code,
                "character ticket cannot fund the collab weapon banner");
            AssertInterruptedFreeDrawRecovers(harness, uid, 5612, free[21000901].Id, 48_608,
                playerSaves, characterSaves, inventorySaves);
            int tenTicketId = free[21000902].Id, weaponTicketId = free[21000903].Id;
            DrawDrawCardResponse ten = DrawWithTicket(harness, 5612, 10, tenTicketId, 48_611, "collab ten ticket");
            AssertEqual((0, 10), (ten.Code, ten.RewardGoodsList.Count), "collab ten ticket funds ten character draws");
            AssertEqual(0, DrawWithTicket(harness, 381, 1, weaponTicketId, 48_612, "collab weapon ticket").Code,
                "collab weapon ticket funds the collab weapon banner");
            Player spentCollab = BsonSerializer.Deserialize<Player>(playerSaves.LastSuccessfulReplacementBson
                ?? throw new InvalidDataException("Collab free draws did not persist."));
            AssertEqual(true, spentCollab.DrawState.PendingDraw is null
                && spentCollab.DrawState.FreeTickets.All(row => row.Count == 0),
                "every collab free ticket is spent exactly once and no draw remains pending after reload");

            // Historical source-backed standard free ticket exercises the paid-display-safe route.
            eventTime = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
            long standardUid = 48_052;
            using LoopbackSessionHarness standard = new(CreateDrawCompatibilityCharacter(standardUid),
                CreateDrawCompatibilityPlayer(standardUid), CreateDrawCompatibilityInventory(standardUid, []),
                "free-standard-ticket-48");
            Player standardPlayer = standard.Session.player;
            grant.Invoke(null, [standardPlayer, 21000802, 1, eventTime]);
            standardPlayer.SaveChecked();
            PlayerDrawTicket ticket = standardPlayer.DrawState.FreeTickets.Single();
            AssertEqual(true, available.Invoke(null, [standardPlayer, ticket.Id, 4, 1, eventTime]) is not null,
                "historical Tidal Chase weapon ticket matches standard group4");
            AssertEqual(true, available.Invoke(null, [standardPlayer, ticket.Id, 4, 10, eventTime]) is null,
                "standard single-use free ticket cannot fund ten pulls");
            AssertEqual(true, available.Invoke(null, [standardPlayer, ticket.Id, 11, 1, eventTime]) is null,
                "standard weapon ticket cannot fund construct group");
            DrawDrawCardResponse DrawStandard(int ticketId, int count, int packetId)
            {
                InvokeRegisteredRequestHandler(nameof(DrawDrawCardRequest), standard.Session, packetId,
                    new DrawDrawCardRequest { DrawId = 301, Count = count, UseDrawTicketId = ticketId });
                return ReadResponsePayload<DrawDrawCardResponse>(standard, packetId,
                    nameof(DrawDrawCardResponse), "standard free-ticket draw", maxPacketsToRead: 24);
            }
            AssertEqual(1, DrawStandard(ticket.Id, 10, 48_602).Code,
                "ten-draw attempt rejects single-use ticket before rewarding");
            AssertEqual(1, ticket.Count, "rejected ten-draw attempt preserves stock");
            AssertInterruptedFreeDrawRecovers(standard, standardUid, 301, ticket.Id, 48_603,
                playerSaves, characterSaves, inventorySaves);
            while (standard.TryReadAvailablePacket("completed login notifications", out _)) { }
            grant.Invoke(null, [standard.Session.player, 21000802, 1, eventTime]);
            PlayerDrawTicket secondTicket = standard.Session.player.DrawState.FreeTickets[^1];
            AssertEqual(true, secondTicket.Id != ticket.Id, "separate free ticket grant gets a new serial");
            PlayerDrawPityRound weaponRound = standard.Session.player.DrawState.PityRounds[4];
            weaponRound.Misses = weaponRound.Limit - 1;
            standard.Session.player.SaveChecked();
            InvokeRegisteredRequestHandler(nameof(DrawDrawCardRequest), standard.Session, 48_606,
                new DrawDrawCardRequest { DrawId = 301, Count = 1, UseDrawTicketId = secondTicket.Id });
            Packet equipPacket = standard.ReadPacket("free weapon draw equip acquisition push");
            AssertEqual(Packet.ContentType.Push, equipPacket.Type, "free weapon equip packet type");
            Packet.Push equipPush = MessagePackSerializer.Deserialize<Packet.Push>(equipPacket.Content);
            AssertEqual(nameof(NotifyEquipDataList), equipPush.Name, "free weapon equip notification");
            NotifyEquipDataList equipment = MessagePackSerializer.Deserialize<NotifyEquipDataList>(equipPush.Content);
            EquipData acquired = equipment.EquipDataList.Single();
            AssertEqual(true, acquired.IsRecycle, "free weapon draw advertises paid-draw recycle option");
            AssertEqual(false, standard.Session.character.Equips.Single(row => row.Id == acquired.Id).IsRecycle,
                "free draw presentation flag does not mark stored equipment for recycling");
            AssertEqual(0, ReadResponsePayload<DrawDrawCardResponse>(standard, 48_606,
                nameof(DrawDrawCardResponse), "free weapon equipment draw", maxPacketsToRead: 24).Code,
                "free weapon draw response succeeds after acquisition push");
            AssertEqual(0, secondTicket.Count, "successful draw spends exactly the second free ticket");
            AssertEqual(true, standard.Session.character.AppliedRewardClaims.Contains(
                $"free-draw:{standardUid}:{secondTicket.Id}:1"),
                "second ticket grants through its own claim rather than reusing the recovered claim");
            Character storedEquipment = BsonSerializer.Deserialize<Character>(characterSaves.LastSuccessfulReplacementBson
                ?? throw new InvalidDataException("Free weapon equipment did not persist."));
            AssertEqual(false, storedEquipment.Equips.Single(row => row.Id == acquired.Id).IsRecycle,
                "free draw recycle presentation never contaminates persistent equipment");

            // A lost free-draw intent write must still spend the ticket exactly once: the frozen retry
            // replays its own outcome instead of taking a second ticket from stock.
            grant.Invoke(null, [standard.Session.player, 21000802, 1, eventTime]);
            PlayerDrawTicket lostIntentTicket = standard.Session.player.DrawState.FreeTickets[^1];
            standard.Session.player.SaveChecked();
            bool failTicketIntent = true;
            playerSaves.BeforeReplaceOne = row =>
            {
                if (failTicketIntent && row.DrawState?.PendingDraw is { } pending && pending.TicketId == lostIntentTicket.Id)
                {
                    failTicketIntent = false;
                    throw new MongoDB.Driver.MongoException("Injected free draw intent write failure.");
                }
            };
            bool ticketIntentLost = false;
            try { _ = DrawWithTicket(standard, 301, 1, lostIntentTicket.Id, 48_613, "lost free intent"); }
            catch (InvalidDataException error) when (error.InnerException is MongoDB.Driver.MongoException)
            { ticketIntentLost = true; }
            finally { playerSaves.BeforeReplaceOne = null; }
            AssertEqual(true, ticketIntentLost, "lost free intent write surfaces to the client");
            AssertEqual(lostIntentTicket.Id, standard.Session.player.DrawState.PendingDraw?.TicketId ?? 0,
                "lost free intent write keeps the frozen ticket draw live");
            AssertEqual(0, DrawWithTicket(standard, 301, 1, lostIntentTicket.Id, 48_614, "replayed free draw").Code,
                "retry replays the frozen free draw without a fresh roll");
            Player freeSpent = BsonSerializer.Deserialize<Player>(playerSaves.LastSuccessfulReplacementBson
                ?? throw new InvalidDataException("Free draw intent replay did not persist."));
            AssertEqual(0, freeSpent.DrawState.FreeTickets.Single(row => row.Id == lostIntentTicket.Id).Count,
                "replayed free draw spends the ticket exactly once");
            AssertEqual(true, freeSpent.DrawState.PendingDraw is null,
                "replayed free draw leaves no pending intent after reload");
        }
        finally { clock.SetValue(null, priorClock); }
    }

    /// <summary>AscNet local policy: a frozen paid draw survives a losing intent write and a lost
    /// completion acknowledgement. Outcome, pity, history, debit plan and task progress persist once,
    /// and a retry that can still afford a pull replays the frozen draw instead of rerolling it.</summary>
    private static void AssertPaidDrawFrozenRecovery()
    {
        var clock = RequiredAscNetGameServerType("AscNet.GameServer.Game.DrawManager")
            .GetField("UtcNow", BindingFlags.NonPublic | BindingFlags.Static)!;
        var priorClock = (Func<DateTimeOffset>)clock.GetValue(null)!;
        clock.SetValue(null, (Func<DateTimeOffset>)(() => new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero)));
        try
        {
            using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
                out RecordingMongoCollectionProxy<Player> playerSaves,
                out RecordingMongoCollectionProxy<Character> characterSaves,
                out RecordingMongoCollectionProxy<Inventory> inventorySaves);
            const int drawId = 5612, groupId = 37, currency = 50017, earned = 50021, unit = 175;
            int[] spendConditions = TableReaderV2.Parse<ConditionTable>()
                .Where(row => row.Type == 11202 && row.Params.Skip(1).Contains(currency) && row.Params.Skip(1).Contains(earned))
                .Select(row => row.Id).ToArray();
            AssertEqual(true, spendConditions.Length > 0, "paid recovery family spend conditions exist");
            long uid = 48_100;
            using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(uid),
                CreateDrawCompatibilityPlayer(uid), CreateDrawCompatibilityInventory(uid,
                [
                    new Item { Id = currency, Count = unit * 4 },
                    new Item { Id = earned, Count = unit * 4 },
                ]), "paid-frozen-recovery-48");
            harness.Session.stage = CreateLoginAccountCompatibilityStage(uid);
            int packetId = 48_950;
            InvokeRegisteredRequestHandler(nameof(DrawGetDrawGroupListRequest), harness.Session, ++packetId,
                new DrawGetDrawGroupListRequest());
            _ = ReadResponsePayload<DrawGetDrawGroupListResponse>(harness, packetId,
                nameof(DrawGetDrawGroupListResponse), "paid recovery group list");
            Player player = harness.Session.player;
            player.SaveChecked();
            Dictionary<int, int> spendBefore = spendConditions.ToDictionary(id => id,
                id => player.MissionProgress.ConditionCounters.GetValueOrDefault(id));
            long Held(Inventory inventory, int itemId) =>
                inventory.Items.FirstOrDefault(item => item.Id == itemId)?.Count ?? 0L;
            void ForceRare()
            {
                foreach (PlayerDrawPityRound round in harness.Session.player.DrawState.PityRounds.Values)
                    round.Misses = round.Limit - 1;
                harness.Session.player.SaveChecked();
            }

            // 1) The intent write is lost before commit: nothing durable, so the funded retry must replay the
            //    frozen outcome instead of charging for a fresh roll.
            bool failIntent = true;
            playerSaves.BeforeReplaceOne = row =>
            {
                if (failIntent && row.DrawState?.PendingDraw is { } pending && pending.DrawId == drawId)
                {
                    failIntent = false;
                    throw new MongoDB.Driver.MongoException("Injected paid intent write failure.");
                }
            };
            bool intentLost = false;
            try { _ = DrawWithTicket(harness, drawId, 1, 0, ++packetId, "lost paid intent"); }
            catch (InvalidDataException error) when (error.InnerException is MongoDB.Driver.MongoException)
            { intentLost = true; }
            finally { playerSaves.BeforeReplaceOne = null; }
            AssertEqual(true, intentLost, "lost paid intent write surfaces to the client");
            PlayerPendingDraw frozen = player.DrawState.PendingDraw
                ?? throw new InvalidDataException("Lost paid intent write must keep the frozen draw live.");
            AssertEqual(true, frozen.Unconfirmed, "unacknowledged paid intent stays replayable");
            string[] pityBefore = PitySnapshot(player);
            DrawInfo frozenInfo = MessagePackSerializer.Deserialize<DrawInfo>(frozen.ClientDrawInfo);
            Player durableIntent = BsonSerializer.Deserialize<Player>(playerSaves.LastSuccessfulReplacementBson
                ?? throw new InvalidDataException("Paid recovery player state missing."));
            AssertEqual(0, durableIntent.DrawState.ProgressByDrawId.GetValueOrDefault(drawId)?.TotalCount ?? 0,
                "lost paid intent write persists no pull counter");
            AssertEqual(0, DrawHistoryCount(durableIntent, groupId, frozenInfo.GroupSubType),
                "lost paid intent write persists no draw history");
            DrawDrawCardResponse replay = DrawWithTicket(harness, drawId, 1, 0, ++packetId, "replayed paid draw");
            AssertEqual(0, replay.Code, "funded retry replays the frozen paid draw");
            AssertEqual(true, replay.RewardGoodsList.Select(row => (row.TemplateId, row.Count, row.ConvertFrom))
                    .SequenceEqual(frozen.Goods.Select(row => (row.TemplateId, row.Count, row.ConvertFrom))),
                "funded retry returns the frozen outcome instead of rerolling");
            AssertEqual(true, replay.ClientDrawInfo is not null && replay.ClientDrawInfo.TotalCount == frozenInfo.TotalCount,
                "funded retry reports the frozen draw info");
            AssertEqual(true, player.DrawState.PendingDraw is null, "replayed paid draw clears its intent");
            AssertEqual(true, pityBefore.SequenceEqual(PitySnapshot(player)), "replayed paid draw does not reroll pity");
            Inventory replayWallet = BsonSerializer.Deserialize<Inventory>(inventorySaves.LastSuccessfulReplacementBson
                ?? throw new InvalidDataException("Paid recovery inventory missing."));
            AssertEqual((unit * 3L, unit * 4L), (Held(replayWallet, earned), Held(replayWallet, currency)),
                "replayed paid draw debits the frozen plan exactly once");
            AssertEqual(true, replayWallet.AppliedRewardClaims.Contains(frozen.ClaimKey),
                "replayed paid draw keeps one inventory receipt");
            Character replayCharacter = BsonSerializer.Deserialize<Character>(characterSaves.LastSuccessfulReplacementBson
                ?? throw new InvalidDataException("Paid recovery character missing."));
            AssertEqual(true, replayCharacter.AppliedRewardClaims.Contains(frozen.ClaimKey),
                "replayed paid draw keeps one character receipt");
            Player replayed = BsonSerializer.Deserialize<Player>(playerSaves.LastSuccessfulReplacementBson
                ?? throw new InvalidDataException("Paid recovery completion missing."));
            AssertEqual(1, replayed.DrawState.ProgressByDrawId.GetValueOrDefault(drawId)?.TotalCount ?? 0,
                "replayed paid draw counts exactly one pull");
            AssertEqual(1, DrawHistoryCount(replayed, groupId, frozenInfo.GroupSubType),
                "replayed paid draw records exactly one history entry");
            foreach (int id in spendConditions)
                AssertEqual(spendBefore[id] + unit, replayed.MissionProgress.ConditionCounters.GetValueOrDefault(id),
                    $"replayed paid draw credits collab spend condition {id} once");
            AssertEqual(frozen.ClaimKey, replayed.DrawState.LastDrawProgressClaim,
                "replayed paid draw records its progress claim");
            AssertEqual(true, replayed.DrawState.PendingDraw is null, "replayed paid draw leaves no pending intent");

            // 2) The completion write is durable server-side but its acknowledgement is lost: the retry must
            //    neither charge nor credit again even though the wallet can still afford a pull.
            ForceRare();
            Dictionary<int, int> spendBeforeAck = spendConditions.ToDictionary(id => id,
                id => player.MissionProgress.ConditionCounters.GetValueOrDefault(id));
            long earnedBeforeAck = Held(harness.Session.inventory, earned);
            long paidBeforeAck = Held(harness.Session.inventory, currency);
            bool sawIntent = false;
            playerSaves.BeforeReplaceOne = row =>
            {
                if (row.DrawState?.PendingDraw is { } pending && pending.DrawId == drawId)
                {
                    sawIntent = true;
                    return;
                }
                // The completion write lands, then its acknowledgement is lost: the durable document below
                // is exactly what a reconnecting client would reload.
                if (sawIntent && row.DrawState?.PendingDraw is null)
                    playerSaves.ThrowAfterReplaceOne = true;
            };
            bool acknowledgementLost = false;
            try { _ = DrawWithTicket(harness, drawId, 1, 0, ++packetId, "lost paid acknowledgement"); }
            catch (InvalidDataException error) when (error.InnerException is MongoDB.Driver.MongoException)
            { acknowledgementLost = true; }
            finally { playerSaves.BeforeReplaceOne = null; }
            AssertEqual(true, acknowledgementLost, "lost paid acknowledgement surfaces to the client");
            AssertEqual(false, playerSaves.ThrowAfterReplaceOne, "acknowledgement loss fires once");
            PlayerPendingDraw acknowledged = player.DrawState.PendingDraw
                ?? throw new InvalidDataException("Lost paid acknowledgement must keep the frozen draw replayable.");
            Player durableAck = BsonSerializer.Deserialize<Player>(playerSaves.LastSuccessfulReplacementBson
                ?? throw new InvalidDataException("Acknowledged completion player document missing."));
            AssertEqual(true, durableAck.DrawState.PendingDraw is null
                    && durableAck.DrawState.LastDrawProgressClaim == acknowledged.ClaimKey,
                "acknowledged completion durably clears the intent with its progress claim");
            AssertEqual(2, durableAck.DrawState.ProgressByDrawId.GetValueOrDefault(drawId)?.TotalCount ?? 0,
                "acknowledged completion counts the second paid pull once");
            foreach (int id in spendConditions)
                AssertEqual(spendBeforeAck[id] + unit, durableAck.MissionProgress.ConditionCounters.GetValueOrDefault(id),
                    $"acknowledged completion credits collab spend condition {id} once");
            string[] pityAcknowledged = PitySnapshot(durableAck);
            DrawDrawCardResponse replayAck = DrawWithTicket(harness, drawId, 1, 0, ++packetId, "replayed paid acknowledgement");
            AssertEqual(0, replayAck.Code, "retry after lost acknowledgement replays the frozen paid draw");
            AssertEqual(true, replayAck.RewardGoodsList.Select(row => (row.TemplateId, row.Count, row.ConvertFrom))
                    .SequenceEqual(acknowledged.Goods.Select(row => (row.TemplateId, row.Count, row.ConvertFrom))),
                "acknowledged retry returns the frozen outcome instead of rerolling");
            AssertEqual((earnedBeforeAck - unit, paidBeforeAck),
                (Held(harness.Session.inventory, earned), Held(harness.Session.inventory, currency)),
                "acknowledged retry does not charge the wallet again");
            AssertEqual(true, pityAcknowledged.SequenceEqual(PitySnapshot(player)), "acknowledged retry does not reroll pity");
            AssertEqual(2, player.DrawState.ProgressByDrawId.GetValueOrDefault(drawId)?.TotalCount ?? 0,
                "acknowledged retry does not count a third pull");
            foreach (int id in spendConditions)
                AssertEqual(spendBeforeAck[id] + unit, player.MissionProgress.ConditionCounters.GetValueOrDefault(id),
                    $"acknowledged retry does not double-credit condition {id}");
            AssertEqual(true, player.DrawState.PendingDraw is null, "acknowledged retry clears the replayed intent");
            Player replayedAck = BsonSerializer.Deserialize<Player>(playerSaves.LastSuccessfulReplacementBson
                ?? throw new InvalidDataException("Acknowledged retry completion missing."));
            AssertEqual(2, replayedAck.DrawState.ProgressByDrawId.GetValueOrDefault(drawId)?.TotalCount ?? 0,
                "acknowledged retry reload keeps exactly two pulls");
            AssertEqual(true, replayedAck.DrawState.PendingDraw is null,
                "acknowledged retry reload leaves no pending intent");
            Inventory ackWallet = BsonSerializer.Deserialize<Inventory>(inventorySaves.LastSuccessfulReplacementBson
                ?? throw new InvalidDataException("Acknowledged retry inventory missing."));
            AssertEqual((earnedBeforeAck - unit, paidBeforeAck), (Held(ackWallet, earned), Held(ackWallet, currency)),
                "acknowledged retry reload persists exactly one debit");

            // 3) A durable intent whose completion never persisted resumes on login exactly once, and a
            //    repeated login cannot duplicate the frozen reward, debit or task progress.
            ForceRare();
            bool sawIntent3 = false;
            playerSaves.BeforeReplaceOne = row =>
            {
                if (row.DrawState?.PendingDraw is { } pending && pending.DrawId == drawId)
                {
                    sawIntent3 = true;
                    return;
                }
                if (sawIntent3 && row.DrawState?.PendingDraw is null)
                    throw new MongoDB.Driver.MongoException("Injected paid completion write failure.");
            };
            bool completionLost = false;
            try { _ = DrawWithTicket(harness, drawId, 1, 0, ++packetId, "interrupted paid draw"); }
            catch (InvalidDataException error) when (error.InnerException is MongoDB.Driver.MongoException)
            { completionLost = true; }
            finally { playerSaves.BeforeReplaceOne = null; }
            AssertEqual(true, completionLost, "interrupted paid completion surfaces to the client");
            Player interrupted = BsonSerializer.Deserialize<Player>(playerSaves.LastSuccessfulReplacementBson
                ?? throw new InvalidDataException("Paid intent state missing."));
            PlayerPendingDraw interruptedPending = interrupted.DrawState.PendingDraw
                ?? throw new InvalidDataException("Interrupted paid draw lost its durable intent.");
            AssertEqual(drawId, interruptedPending.DrawId, "durable paid intent survives reload");
            Character interruptedCharacter = BsonSerializer.Deserialize<Character>(characterSaves.LastSuccessfulReplacementBson
                ?? throw new InvalidDataException("Paid draw character receipt missing."));
            Inventory interruptedInventory = BsonSerializer.Deserialize<Inventory>(inventorySaves.LastSuccessfulReplacementBson
                ?? throw new InvalidDataException("Paid draw inventory receipt missing."));
            AssertEqual(true, interruptedCharacter.AppliedRewardClaims.Contains(interruptedPending.ClaimKey)
                    && interruptedInventory.AppliedRewardClaims.Contains(interruptedPending.ClaimKey),
                "interrupted paid draw keeps durable reward receipts");
            int characterCount = interruptedCharacter.Characters.Count;
            int itemCount = interruptedInventory.Items.Count;
            harness.Session.player = interrupted;
            harness.Session.character = interruptedCharacter;
            harness.Session.inventory = interruptedInventory;
            var loginMethod = RequiredMethod(RequiredAscNetGameServerType("AscNet.GameServer.Handlers.AccountModule"),
                "BuildNotifyLogin", BindingFlags.NonPublic | BindingFlags.Static, [typeof(Session)]);
            NotifyLogin recovered = (NotifyLogin)(loginMethod.Invoke(null, [harness.Session])
                ?? throw new InvalidDataException("Paid draw recovery login was not built."));
            AssertEqual(true, harness.Session.player.DrawState.PendingDraw is null,
                "login completes the durable paid draw intent");
            AssertEqual(characterCount, recovered.CharacterList.Count,
                "login recovers the frozen paid reward before the character snapshot");
            AssertEqual(itemCount, harness.Session.inventory.Items.Count,
                "login receipt replay cannot award extra paid items");
            _ = loginMethod.Invoke(null, [harness.Session]);
            AssertEqual(characterCount, harness.Session.character.Characters.Count,
                "repeated login does not duplicate paid draw rewards");
            Player resumed = BsonSerializer.Deserialize<Player>(playerSaves.LastSuccessfulReplacementBson
                ?? throw new InvalidDataException("Paid draw recovery completion missing."));
            int subType = MessagePackSerializer.Deserialize<DrawInfo>(interruptedPending.ClientDrawInfo).GroupSubType;
            AssertEqual(3, resumed.DrawState.ProgressByDrawId.GetValueOrDefault(drawId)?.TotalCount ?? 0,
                "each paid pull is counted exactly once");
            AssertEqual(3, DrawHistoryCount(resumed, groupId, subType), "each paid pull is recorded once in history");
            foreach (int id in spendConditions)
                AssertEqual(spendBefore[id] + 3 * unit, resumed.MissionProgress.ConditionCounters.GetValueOrDefault(id),
                    $"collab spend condition {id} counts each paid pull once");
            AssertEqual(interruptedPending.ClaimKey, resumed.DrawState.LastDrawProgressClaim,
                "recovered paid draw records its progress claim");
            AssertEqual(true, resumed.DrawState.PendingDraw is null, "recovered paid draw leaves no pending intent");
            AssertEqual((unit * 1L, unit * 4L), (Held(harness.Session.inventory, earned), Held(harness.Session.inventory, currency)),
                "three paid pulls debit the earned stack exactly once each");

            // 4) A request for another banner while a lost-acknowledgement intent awaits replay is refused, and
            //    must not roll that banner: the frozen draw completes once and the new banner stays untouched.
            ForceRare();
            long earnedBeforeMismatch = Held(harness.Session.inventory, earned);
            bool sawIntent4 = false;
            playerSaves.BeforeReplaceOne = row =>
            {
                if (row.DrawState?.PendingDraw is { } pending && pending.DrawId == drawId)
                {
                    sawIntent4 = true;
                    return;
                }
                if (sawIntent4 && row.DrawState?.PendingDraw is null)
                    playerSaves.ThrowAfterReplaceOne = true;
            };
            bool secondAcknowledgementLost = false;
            try { _ = DrawWithTicket(harness, drawId, 1, 0, ++packetId, "lost paid acknowledgement again"); }
            catch (InvalidDataException error) when (error.InnerException is MongoDB.Driver.MongoException)
            { secondAcknowledgementLost = true; }
            finally { playerSaves.BeforeReplaceOne = null; }
            AssertEqual(true, secondAcknowledgementLost, "second lost paid acknowledgement surfaces to the client");
            DrawDrawCardResponse mismatched = DrawWithTicket(harness, 381, 1, 0, ++packetId, "mismatched paid draw");
            AssertEqual(1, mismatched.Code, "mismatched request is refused while a frozen draw awaits replay");
            AssertEqual(0L, Held(harness.Session.inventory, 50018), "mismatched request does not roll its own banner");
            AssertEqual(earnedBeforeMismatch - unit, Held(harness.Session.inventory, earned),
                "frozen draw completes instead of charging the mismatched banner");
            AssertEqual(0, harness.Session.player.DrawState.ProgressByDrawId.GetValueOrDefault(381)?.TotalCount ?? 0,
                "mismatched banner gains no pull counter");
            AssertEqual(4, harness.Session.player.DrawState.ProgressByDrawId.GetValueOrDefault(drawId)?.TotalCount ?? 0,
                "frozen paid draw completes once even for a mismatched request");
            AssertEqual(true, harness.Session.player.DrawState.PendingDraw is null,
                "mismatched request still clears the replayed intent");

            static string[] PitySnapshot(Player snapshot) => snapshot.DrawState.PityRounds.OrderBy(round => round.Key)
                .Select(round => $"{round.Key}:{round.Value.Misses}/{round.Value.Limit}/{round.Value.HasObtainedRare}/" +
                    $"{round.Value.GuaranteedTarget}/{round.Value.LowerMisses}")
                .ToArray();
            static int DrawHistoryCount(Player snapshot, int group, int groupSubType) =>
                snapshot.DrawState.HistoryByGroup.GetValueOrDefault(group)
                    ?.HistoryBySubType.GetValueOrDefault(groupSubType)?.Count ?? 0;
        }
        finally { clock.SetValue(null, priorClock); }
    }

    private static DrawDrawCardResponse DrawWithTicket(LoopbackSessionHarness harness, int drawId, int count,
        int ticketId, int packetId, string label)
    {
        InvokeRegisteredRequestHandler(nameof(DrawDrawCardRequest), harness.Session, packetId,
            new DrawDrawCardRequest { DrawId = drawId, Count = count, UseDrawTicketId = ticketId });
        return ReadResponsePayload<DrawDrawCardResponse>(harness, packetId, nameof(DrawDrawCardResponse), label,
            maxPacketsToRead: 40);
    }

    // Uses packet ids packetId and packetId + 2.
    private static void AssertInterruptedFreeDrawRecovers(LoopbackSessionHarness harness, long uid, int drawId,
        int ticketId, int packetId, RecordingMongoCollectionProxy<Player> playerSaves,
        RecordingMongoCollectionProxy<Character> characterSaves, RecordingMongoCollectionProxy<Inventory> inventorySaves)
    {
        string label = $"draw {drawId} free ticket";
        playerSaves.BeforeReplaceOne = row =>
        {
            if (row.DrawState.PendingDraw is null
                && row.DrawState.FreeTickets.Any(value => value.Id == ticketId && value.Count == 0))
                throw new MongoDB.Driver.MongoException("Injected post-grant free draw player save failure.");
        };
        bool failedAfterGrant = false;
        try { _ = DrawWithTicket(harness, drawId, 1, ticketId, packetId, label); }
        catch (InvalidDataException error) when (error.InnerException is MongoDB.Driver.MongoException)
        { failedAfterGrant = true; }
        finally { playerSaves.BeforeReplaceOne = null; }
        AssertEqual(true, failedAfterGrant, $"{label}: failed final player save surfaces after durable reward receipt");
        Player interrupted = BsonSerializer.Deserialize<Player>(playerSaves.LastSuccessfulReplacementBson
            ?? throw new InvalidDataException("Free draw intent did not persist."));
        AssertEqual(0, interrupted.DrawState.FreeTickets.Single(row => row.Id == ticketId).Count,
            $"{label}: ticket debit persists before reward grant");
        AssertEqual(drawId, interrupted.DrawState.PendingDraw?.DrawId ?? 0,
            $"{label}: failed final player save preserves frozen draw to resume after relog");
        string claim = interrupted.DrawState.PendingDraw!.ClaimKey;
        Character awardedCharacter = BsonSerializer.Deserialize<Character>(characterSaves.LastSuccessfulReplacementBson
            ?? throw new InvalidDataException("Free draw character receipt did not persist."));
        Inventory awardedInventory = BsonSerializer.Deserialize<Inventory>(inventorySaves.LastSuccessfulReplacementBson
            ?? throw new InvalidDataException("Free draw inventory receipt did not persist."));
        AssertEqual(true, awardedCharacter.AppliedRewardClaims.Contains(claim)
            && awardedInventory.AppliedRewardClaims.Contains(claim),
            $"{label}: reward receipts survive interrupted final player save");
        int equipCount = awardedCharacter.Equips.Count;
        int characterCount = awardedCharacter.Characters.Count;
        int itemCount = awardedInventory.Items.Count;
        harness.Session.player = interrupted;
        harness.Session.character = awardedCharacter;
        harness.Session.inventory = awardedInventory;
        harness.Session.stage = CreateLoginAccountCompatibilityStage(uid);
        var loginMethod = RequiredMethod(RequiredAscNetGameServerType("AscNet.GameServer.Handlers.AccountModule"),
            "BuildNotifyLogin", BindingFlags.NonPublic | BindingFlags.Static, [typeof(Session)]);
        NotifyLogin recovered = (NotifyLogin)(loginMethod.Invoke(null, [harness.Session])
            ?? throw new InvalidDataException("Free draw recovery login was not built."));
        AssertEqual(equipCount, recovered.EquipList.Count,
            $"{label}: login recovers frozen reward before character/equipment snapshot");
        AssertEqual(itemCount, harness.Session.inventory.Items.Count,
            $"{label}: login receipt replay cannot award extra inventory items");
        AssertEqual(true, harness.Session.player.DrawState.PendingDraw is null,
            $"{label}: login completes and clears durable pending free draw");
        _ = loginMethod.Invoke(null, [harness.Session]);
        AssertEqual((equipCount, characterCount),
            (harness.Session.character.Equips.Count, harness.Session.character.Characters.Count),
            $"{label}: repeated login does not duplicate draw rewards");
        AssertEqual(1, DrawWithTicket(harness, drawId, 1, ticketId, packetId + 2, label).Code,
            $"{label}: spent instance cannot repeat draw");
        Player spent = BsonSerializer.Deserialize<Player>(playerSaves.LastSuccessfulReplacementBson
            ?? throw new InvalidDataException("Free ticket consumption did not persist."));
        AssertEqual(0, spent.DrawState.FreeTickets.Single(row => row.Id == ticketId).Count,
            $"{label}: free ticket remains spent after BSON reload");
        while (harness.TryReadAvailablePacket("completed login notifications", out _)) { }
    }

    // AscNet local policy (not retail-recovered): collab character/weapon/CUB banners spend 50017/50018/50019.
    private static void AssertCollabPaidDraws()
    {
        var clock = RequiredAscNetGameServerType("AscNet.GameServer.Game.DrawManager")
            .GetField("UtcNow", BindingFlags.NonPublic | BindingFlags.Static)!;
        var priorClock = (Func<DateTimeOffset>)clock.GetValue(null)!;
        DateTimeOffset now = default;
        clock.SetValue(null, (Func<DateTimeOffset>)(() => now));
        try
        {
            using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
                out var playerSaves, out var characterSaves, out var inventorySaves);
            long uid = 48_070;
            int packetId = 48_700;
            foreach ((int drawId, int groupId, int currency, int unit, RewardType type) in new[]
            {
                (5612, 37, 50017, 175, RewardType.Character),
                (381, 39, 50018, 250, RewardType.Equip),
                (7068, 40, 50019, 250, RewardType.Partner),
            })
            {
                now = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
                uid++;
                int earned = currency + 4; // 50021/50022/50023 earned family shares the ItemCombine balance.
                int otherCategory = currency == 50017 ? 50018 : 50017;
                using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(uid),
                    CreateDrawCompatibilityPlayer(uid), CreateDrawCompatibilityInventory(uid,
                    [
                        new Item { Id = currency, Count = unit },
                        new Item { Id = earned, Count = 2 * unit - 1 },
                        new Item { Id = otherCategory, Count = unit * 10 },
                    ]), $"collab-paid-{drawId}");
                harness.Session.stage = CreateLoginAccountCompatibilityStage(uid);
                long Held(Inventory inventory, int itemId) =>
                    inventory.Items.SingleOrDefault(item => item.Id == itemId)?.Count ?? 0L;
                void ForceRare()
                {
                    foreach (PlayerDrawPityRound round in harness.Session.player.DrawState.PityRounds.Values)
                        round.Misses = round.Limit - 1;
                    harness.Session.player.SaveChecked();
                }
                InvokeRegisteredRequestHandler(nameof(DrawGetDrawGroupListRequest), harness.Session, ++packetId,
                    new DrawGetDrawGroupListRequest());
                List<DrawGroupInfo> groups = ReadResponsePayload<DrawGetDrawGroupListResponse>(harness, packetId,
                    nameof(DrawGetDrawGroupListResponse), "collab group list").DrawGroupInfoList;
                AssertEqual(false, groups.Any(group => group.Id == 38), "collab Fate group38 is never listed");
                AssertEqual(currency, groups.Single(group => group.Id == groupId).UseItemId,
                    $"group {groupId} advertises its policy currency");
                InvokeRegisteredRequestHandler(nameof(DrawGetDrawInfoListRequest), harness.Session, ++packetId,
                    new DrawGetDrawInfoListRequest { GroupId = groupId });
                DrawInfo info = ReadResponsePayload<DrawGetDrawInfoListResponse>(harness, packetId,
                    nameof(DrawGetDrawInfoListResponse), "collab draw info").DrawInfoList.Single(row => row.Id == drawId);
                AssertEqual((currency, unit, true), (info.UseItemId, info.UseItemCount,
                    Inventory.IsValidClientItemId(info.UseItemId)), $"draw {drawId} advertises a valid client cost item");

                ForceRare();
                int saves = playerSaves.ReplaceOneCalls + characterSaves.ReplaceOneCalls + inventorySaves.ReplaceOneCalls;
                AssertEqual(1, DrawWithTicket(harness, drawId, 1, otherCategory, ++packetId, "wrong collab category").Code,
                    $"draw {drawId} rejects another category's owned collab currency");
                AssertEqual(1, DrawWithTicket(harness, drawId, 1, earned, ++packetId, "non-canonical request id").Code,
                    $"draw {drawId} request identity stays the canonical paid id");
                AssertEqual(1, DrawWithTicket(harness, drawId, 10, 0, ++packetId, "insufficient collab currency").Code,
                    $"draw {drawId} rejects ten draws beyond the combined balance");
                AssertEqual((saves, (long)unit, 2L * unit - 1, true), (playerSaves.ReplaceOneCalls + characterSaves.ReplaceOneCalls
                    + inventorySaves.ReplaceOneCalls, Held(harness.Session.inventory, currency), Held(harness.Session.inventory, earned),
                    harness.Session.player.DrawState.PityRounds.Values.All(round => round.Misses == round.Limit - 1)),
                    $"draw {drawId} rejected payments persist nothing and keep wallet and pity");

                // Source conditions counting both legs of this family (Condition 140375/140376 for 50017+50021 only).
                int[] spendConditions = TableReaderV2.Parse<ConditionTable>()
                    .Where(row => row.Type == 11202 && row.Params.Skip(1).Contains(currency) && row.Params.Skip(1).Contains(earned))
                    .Select(row => row.Id).ToArray();
                Dictionary<int, int> spentBefore = spendConditions.ToDictionary(id => id,
                    id => harness.Session.player.MissionProgress.ConditionCounters.GetValueOrDefault(id));
                // Earned-only, then mixed: lower-priority earned stock is spent before paid stock.
                foreach ((long paidLeft, long earnedLeft, string label) in new[]
                    { ((long)unit, unit - 1L, "earned-only"), (unit - 1L, 0L, "mixed") })
                {
                    ForceRare();
                    DrawDrawCardResponse paid = DrawWithTicket(harness, drawId, 1, 0, ++packetId, $"{label} collab draw");
                    AssertEqual((0, currency), (paid.Code, paid.ClientDrawInfo?.UseItemId ?? 0),
                        $"draw {drawId} {label} pull succeeds and echoes its canonical currency");
                    RewardGoods rare = paid.RewardGoodsList.Single();
                    AssertEqual(true, rare.RewardType == (int)type || rare.ConvertFrom > 0,
                        $"draw {drawId} {label} pity-forced rare is the banner's reward type (or its duplicate conversion)");
                    Inventory wallet = BsonSerializer.Deserialize<Inventory>(inventorySaves.LastSuccessfulReplacementBson
                        ?? throw new InvalidDataException("Collab paid draw inventory did not persist."));
                    AssertEqual((paidLeft, earnedLeft, unit * 10L), (Held(wallet, currency), Held(wallet, earned),
                        Held(wallet, otherCategory)), $"draw {drawId} {label} physical debits after BSON reload");
                }
                Player progressed = BsonSerializer.Deserialize<Player>(playerSaves.LastSuccessfulReplacementBson
                    ?? throw new InvalidDataException("Collab draw spend progress did not persist."));
                foreach (int id in spendConditions)
                    AssertEqual(spentBefore[id] + 2 * unit, progressed.MissionProgress.ConditionCounters.GetValueOrDefault(id),
                        $"draw {drawId} condition {id} counts each physical debit leg exactly once after BSON reload");

                // Source Item FromConfig timeliness (StartTime + Duration); both collab families are timed.
                static long ConfigExpiry(int itemId) =>
                    TableReaderV2.Parse<ItemTable>().Single(row => row.Id == itemId) is { TimelinessType: 1, Duration: > 0 } row
                    && DateTimeOffset.TryParseExact(row.StartTime, "yyyy/M/d H:mm",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                        out DateTimeOffset start) ? start.ToUnixTimeSeconds() + row.Duration!.Value
                        : throw new InvalidDataException($"Item {itemId} has no config expiry.");
                long firstExpiry = Math.Min(ConfigExpiry(currency), ConfigExpiry(earned));
                long lastExpiry = Math.Max(ConfigExpiry(currency), ConfigExpiry(earned));
                // Exactly one pull's cost per family on the single physical stack of each id.
                foreach (int id in new[] { currency, earned })
                {
                    Item? stack = harness.Session.inventory.Items.SingleOrDefault(item => item.Id == id);
                    if (stack is null) harness.Session.inventory.Items.Add(new Item { Id = id, Count = unit });
                    else stack.Count = unit;
                }
                harness.Session.inventory.SaveChecked();
                ForceRare();

                now = DateTimeOffset.FromUnixTimeSeconds(lastExpiry);
                InvokeRegisteredRequestHandler(nameof(DrawGetDrawInfoListRequest), harness.Session, ++packetId,
                    new DrawGetDrawInfoListRequest { GroupId = groupId });
                AssertEqual(true, ReadResponsePayload<DrawGetDrawInfoListResponse>(harness, packetId,
                    nameof(DrawGetDrawInfoListResponse), "collab draw info at expiry").DrawInfoList.Any(row => row.Id == drawId),
                    $"draw {drawId} catalog stays open when its currency expires");
                byte[][] snapshot = [harness.Session.player.ToBson(), harness.Session.character.ToBson(), harness.Session.inventory.ToBson()];
                saves = playerSaves.ReplaceOneCalls + characterSaves.ReplaceOneCalls + inventorySaves.ReplaceOneCalls;
                AssertEqual(1, DrawWithTicket(harness, drawId, 1, 0, ++packetId, "expired collab currency").Code,
                    $"draw {drawId} rejects config-expired paid and earned stock");
                AssertEqual(true, saves == playerSaves.ReplaceOneCalls + characterSaves.ReplaceOneCalls + inventorySaves.ReplaceOneCalls
                    && snapshot[0].SequenceEqual(harness.Session.player.ToBson())
                    && snapshot[1].SequenceEqual(harness.Session.character.ToBson())
                    && snapshot[2].SequenceEqual(harness.Session.inventory.ToBson()),
                    $"draw {drawId} expired-currency rejection mutates no state, reward or cost");

                now = DateTimeOffset.FromUnixTimeSeconds(firstExpiry - 1);
                foreach (string leg in new[] { "earned", "paid" })
                {
                    ForceRare();
                    AssertEqual(0, DrawWithTicket(harness, drawId, 1, 0, ++packetId, $"{leg} pull before expiry").Code,
                        $"draw {drawId} {leg} stock still funds a pull one second before expiry");
                }
                Inventory drained = BsonSerializer.Deserialize<Inventory>(inventorySaves.LastSuccessfulReplacementBson
                    ?? throw new InvalidDataException("Pre-expiry collab draw inventory did not persist."));
                AssertEqual((0L, 0L), (Held(drained, currency), Held(drained, earned)),
                    $"draw {drawId} pre-expiry pulls debit earned then paid stock exactly");
            }
        }
        finally { clock.SetValue(null, priorClock); }
    }
}
