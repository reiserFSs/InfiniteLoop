using System.Reflection;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.condition;
using AscNet.Table.V2.share.fuben.stronghold;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace AscNet.Test;

internal partial class Program
{
    private static void ValidateStrongholdRolloverCompatibility()
    {
        Type module = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.StrongholdModule");
        MethodInfo prepare = RequiredMethod(module, "PrepareLogin", BindingFlags.Static | BindingFlags.NonPublic,
            [typeof(Player), typeof(DateTimeOffset)]);
        MethodInfo build = RequiredMethod(module, "BuildLoginData", BindingFlags.Static | BindingFlags.NonPublic,
            [typeof(Player), typeof(DateTimeOffset)]);
        long loginClock = 0;
        void Prepare(Player player, long clock)
        {
            loginClock = clock;
            prepare.Invoke(null, [player, DateTimeOffset.FromUnixTimeSeconds(clock)]);
        }
        NotifyStrongholdLoginData Login(Player player) =>
            (NotifyStrongholdLoginData)build.Invoke(null, [player, DateTimeOffset.FromUnixTimeSeconds(loginClock)])!;
        string Snapshot<T>(T value) => Convert.ToHexString(value.ToBson());

        List<StrongholdActivityTable> activities = TableReaderV2.Parse<StrongholdActivityTable>();
        StrongholdActivityTable special = activities.Single(row => row.Id == 79);
        StrongholdActivityTable fallback = activities.Single(row => row.Id == 1);
        AssertEqual(684000, special.OneCycleSeconds, "Norman saved special cycle duration");
        AssertEqual(1209600, fallback.OneCycleSeconds, "Norman client unknown-ID default duration");
        const uint savedBegin = 1_788_021_239;
        long specialEnd = savedBegin + (long)special.OneCycleSeconds;
        AssertEqual(new DateTime(2026, 9, 6), DateTimeOffset.FromUnixTimeSeconds(specialEnd).UtcDateTime.Date,
            "Norman saved special cycle ends September 6");
        StrongholdLevelTable level = TableReaderV2.Parse<StrongholdLevelTable>().Single(row => row.Id == 1);
        List<StrongholdRewardTable> rewards = TableReaderV2.Parse<StrongholdRewardTable>();
        Dictionary<int, ConditionTable> conditions = TableReaderV2.Parse<ConditionTable>().ToDictionary(row => row.Id);
        StrongholdRewardTable[] groupRewards = rewards.Where(row => row.LevelId == level.Id
            && conditions[row.Condition].Type == 10131).Take(2).ToArray();
        if (groupRewards.Length != 2)
            throw new InvalidDataException("Norman rollover requires two authoritative group-clear reward conditions.");

        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
            out RecordingMongoCollectionProxy<Player> saves, out _, out _);
        long uid = 48_901;
        Player Fresh(int id, uint begin)
        {
            Player player = CreateDrawCompatibilityPlayer(uid++);
            player.PlayerData.Level = 80;
            player.Stronghold.ActivityId = id;
            player.Stronghold.BeginTime = begin;
            Prepare(player, begin);
            return player;
        }

        foreach (int oldId in new[] { special.Id, activities.Max(row => row.Id) + 7 })
        {
            Player player = Fresh(oldId, savedBegin);
            StrongholdState state = player.Stronghold;
            StrongholdRewardTable eligible = groupRewards[0];
            StrongholdRewardTable ineligible = groupRewards[1];
            int completedGroup = conditions[eligible.Condition].Params[0];
            StrongholdGroupStageData stages = state.GroupStageDatas.Single(row => row.Id == completedGroup);
            state.GroupInfos.Single(row => row.Id == completedGroup).FinishStageIds = stages.StageIds.Select(id => (int)id).ToList();
            state.FinishGroupIds = [completedGroup];
            state.FinishGroupInfos = [new() { Id = completedGroup, UsedElectricEnergy = 23, UsedSystemElectricEnergy = 41 }];
            state.HistoryFinishGroupInfos = [new() { Id = completedGroup, UsedElectricEnergy = 7, UsedSystemElectricEnergy = 11 }];
            string history = Snapshot(state.HistoryFinishGroupInfos.Single());
            state.RewardIds = rewards.Where(row => row.Id != eligible.Id && row.Id != ineligible.Id).Select(row => row.Id).Order().ToList();
            state.ClaimedRewardIds = state.RewardIds.ToList();
            state.PendingGroupId = completedGroup;
            state.PendingStageId = (int)stages.StageIds[0];
            state.TeamInfos[1] = new() { Id = 1, CharacterInfos = [new() { Id = 1_021_001, Pos = 1 }] };
            state.FightTeamInfos[completedGroup] = [state.TeamInfos[1]];
            state.ElectricCharacterIds = [1_021_001];
            state.AssistCharacterId = 1_021_001;
            state.SetAssistCharacterTime = (int)savedBegin;
            state.BorrowCount = 2;
            state.ElectricEnergy = 123;
            state.Endurance = 3;
            state.MineralLeft = 17;
            state.TotalMineral = 29;
            state.CurDay = 4;
            state.StayDays = [1, 2];
            state.MineRecords = [new() { Day = 1, MinerCount = 1, MineralCount = 29 }];
            Character roster = CreateDrawCompatibilityCharacter(player.PlayerData.Id);
            Inventory inventory = CreateDrawCompatibilityInventory(player.PlayerData.Id, [new Item { Id = Inventory.Coin, Count = 173 }]);
            using LoopbackSessionHarness harness = new(roster, player, inventory, $"norman-rollover-{oldId}");
            string rosterBefore = Snapshot(roster);
            string inventoryBefore = Snapshot(inventory);
            string before = Snapshot(player);
            long expiry = savedBegin + (long)(oldId == special.Id ? special.OneCycleSeconds : fallback.OneCycleSeconds);
            Prepare(player, expiry - 1);
            AssertEqual(before, Snapshot(player), $"Norman {oldId} stays unchanged one second before expiry");
            AssertEqual(oldId, Login(player).Id, $"Norman {oldId} login retains unexpired instance");

            Prepare(player, expiry);
            NotifyStrongholdLoginData login = Login(player);
            int successor = Math.Max(oldId + 1, activities.Max(row => row.Id) + 1);
            AssertEqual(successor, login.Id, $"Norman {oldId} boundary login selects successor");
            AssertEqual((uint)expiry, login.BeginTime, $"Norman {oldId} successor anchors at previous end");
            AssertEqual((int)expiry, login.FightBeginTime, $"Norman {oldId} successor fight begins at cycle boundary");
            AssertEqual(oldId, login.LastResultRecord.Id, $"Norman {oldId} login retains expired-cycle result identity");
            AssertEqual(0, login.FinishGroupIds.Count, $"Norman {oldId} clears current completed groups");
            AssertEqual(0, login.FinishGroupInfos.Count, $"Norman {oldId} clears current energy results");
            AssertEqual(true, login.GroupInfos.All(group => group.FinishStageIds.Count == 0), $"Norman {oldId} clears actual stage progress");
            AssertEqual(true, login.GroupStageDatas.Any(group => group.Id == completedGroup && group.StageIds.Count > 0),
                $"Norman {oldId} successor retains playable table-derived stages");
            AssertEqual(history, Snapshot(login.HistoryFinishGroupInfos.Single(row => row.Id == completedGroup)),
                $"Norman {oldId} preserves lifetime best history");
            AssertEqual(true, login.RewardIds.Count == 0 && player.Stronghold.ClaimedRewardIds.Count == 0,
                $"Norman {oldId} starts the new cycle with no achievement claims (claims are per cycle)");
            AssertEqual(0, player.Stronghold.PendingGroupId, $"Norman {oldId} clears pending group");
            AssertEqual(0, player.Stronghold.PendingStageId, $"Norman {oldId} clears pending stage");
            AssertEqual(0, login.TeamInfos.Count, $"Norman {oldId} IsClearTeam clears saved team");
            AssertEqual(0, player.Stronghold.FightTeamInfos.Count, $"Norman {oldId} clears fight teams");
            AssertEqual((uint)level.InitElectricEnergy, login.ElectricEnergy, $"Norman {oldId} restores configured electricity");
            AssertEqual(level.InitEndurance, login.Endurance, $"Norman {oldId} restores configured endurance");
            AssertEqual(0, login.MineralLeft, $"Norman {oldId} clears current mineral balance");
            AssertEqual(0, login.TotalMineral, $"Norman {oldId} clears current mineral total");
            AssertEqual(rosterBefore, Snapshot(harness.Session.character), $"Norman {oldId} preserves roster");
            AssertEqual(inventoryBefore, Snapshot(harness.Session.inventory), $"Norman {oldId} settlement does not directly credit inventory");
            AssertEqual(true, player.Mails.Any(mail => mail.RewardClaimKey?.EndsWith($":achievement:{eligible.Id}") == true), $"Norman {oldId} settles eligible unclaimed group reward by mail");
            AssertEqual(false, player.Mails.Any(mail => mail.RewardClaimKey?.EndsWith($":achievement:{ineligible.Id}") == true), $"Norman {oldId} does not settle uncompleted-group reward");
            AssertEqual(2, player.Mails.Count, $"Norman {oldId} produces achievement and cycle-result mails");
            PlayerMail achievementMail = player.Mails.Single(mail => mail.RewardClaimKey == $"stronghold:{player.PlayerData.Id}:{oldId}:{savedBegin}:achievement:{eligible.Id}");
            var reward = TableReaderV2.Parse<AscNet.Table.V2.share.reward.RewardTable>().Single(row => row.Id == eligible.RewardId);
            var goods = TableReaderV2.Parse<AscNet.Table.V2.share.reward.RewardGoodsTable>()
                .Where(row => reward.SubIds.Contains(row.Id)).ToList();
            string expectedGoods = string.Join(",", goods.GroupBy(row => row.TemplateId).OrderBy(group => group.Key)
                .Select(group => $"{group.Key}:{group.Sum(row => row.Count)}"));
            string actualGoods = string.Join(",", achievementMail.RewardGoodsList!
                .GroupBy(row => row.TemplateId).OrderBy(group => group.Key)
                .Select(group => $"{group.Key}:{group.Sum(row => row.Count)}"));
            AssertEqual(expectedGoods, actualGoods, $"Norman {oldId} settlement mail grants exactly the eligible table reward");

            Player reloaded = BsonSerializer.Deserialize<Player>((saves.LastReplacement
                ?? throw new InvalidDataException("Norman rollover did not persist Player.")).ToBson());
            string persisted = Snapshot(reloaded);
            Prepare(reloaded, expiry);
            AssertEqual(persisted, Snapshot(reloaded), $"Norman {oldId} same-clock BSON relogin is idempotent");
            AssertEqual(successor, Login(reloaded).Id, $"Norman {oldId} BSON relogin retains successor");
            AssertEqual(2, reloaded.Mails.Count, $"Norman {oldId} BSON relogin does not duplicate settlement mails");

            harness.Session.player = reloaded;
            using MongoCollectionOverride stageMongo = MongoCollectionOverride.InstallForStudyProgressionCompatibility(out _);
            harness.Session.stage = CreateLoginAccountCompatibilityStage(reloaded.PlayerData.Id);
            StrongholdGroupStageData nextStages = reloaded.Stronghold.GroupStageDatas.First(group => group.StageIds.Count > 1);
            reloaded.Stronghold.PendingGroupId = nextStages.Id;
            reloaded.Stronghold.PendingStageId = (int)nextStages.StageIds[0];
            string previousResult = Snapshot(Login(reloaded).LastResultRecord);
            RequiredMethod(module, "Settle", BindingFlags.Static | BindingFlags.NonPublic,
                [typeof(Player), typeof(bool), typeof(AscNet.GameServer.Session), typeof(bool)])
                .Invoke(null, [reloaded, true, harness.Session, true]);
            AssertEqual(true, Login(reloaded).GroupInfos.Single(group => group.Id == nextStages.Id)
                .FinishStageIds.Contains((int)nextStages.StageIds[0]), $"Norman {oldId} new-cycle battle records actual stage clear");
            AssertEqual(previousResult, Snapshot(Login(reloaded).LastResultRecord),
                $"Norman {oldId} new-cycle battle does not mutate previous-cycle login result");
            Prepare(reloaded, expiry + fallback.OneCycleSeconds);
            AssertEqual(successor, Login(reloaded).LastResultRecord.Id, $"Norman {oldId} next rollover publishes the newly played cycle");
            AssertEqual(1, Login(reloaded).LastResultRecord.FinishCount, $"Norman {oldId} next rollover reports only new-cycle clears");
        }

        Player missed = Fresh(special.Id, savedBegin);
        long lateClock = specialEnd + 3L * fallback.OneCycleSeconds + 17;
        Prepare(missed, lateClock);
        AssertEqual(83, Login(missed).Id, "Norman missed cycles skip directly to the current instance");
        AssertEqual((uint)(specialEnd + 3L * fallback.OneCycleSeconds), Login(missed).BeginTime,
            "Norman missed cycles advance from the persisted boundary, not login time");
        AssertEqual(special.Id, Login(missed).LastResultRecord.Id, "Norman skipped empty cycles do not overwrite the played-cycle result");
        string missedSnapshot = Snapshot(missed);
        Prepare(missed, lateClock);
        AssertEqual(missedSnapshot, Snapshot(missed), "Norman missed-cycle advancement happens only once");

        Player unknown = Fresh(activities.Max(row => row.Id) + 9, savedBegin);
        string unknownSnapshot = Snapshot(unknown);
        Prepare(unknown, specialEnd);
        AssertEqual(unknownSnapshot, Snapshot(unknown), "Norman unknown instance uses default duration rather than special79 duration");
        Player future = Fresh(special.Id, savedBegin);
        string futureSnapshot = Snapshot(future);
        Prepare(future, savedBegin - 1L);
        AssertEqual(futureSnapshot, Snapshot(future), "Norman future persisted begin is not rebased or reset");
        AssertEqual(0U, Login(future).BeginTime, "Norman future cycle is hidden from login without changing its persisted anchor");
        ValidateStrongholdRolloverMailCompatibility();
        ValidateStrongholdHistoryRewardSchemaEdge();
    }

    private static void ValidateStrongholdRolloverMailCompatibility()
    {
        AscNet.GameServer.PacketFactory.LoadPacketHandlers();
        Type module = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.StrongholdModule");
        MethodInfo prepare = RequiredMethod(module, "PrepareLogin", BindingFlags.Static | BindingFlags.NonPublic,
            [typeof(Player), typeof(DateTimeOffset)]);
        MethodInfo rewardType = RequiredMethod(RequiredAscNetGameServerType("AscNet.GameServer.Handlers.RewardHandler"),
            "GetRewardType", BindingFlags.Static | BindingFlags.Public, [typeof(AscNet.Table.V2.share.reward.RewardGoodsTable)]);
        var rewardRows = TableReaderV2.Parse<AscNet.Table.V2.share.reward.RewardTable>().ToDictionary(row => row.Id);
        var goodsRows = TableReaderV2.Parse<AscNet.Table.V2.share.reward.RewardGoodsTable>().ToDictionary(row => row.Id);
        var conditions = TableReaderV2.Parse<ConditionTable>().ToDictionary(row => row.Id);
        var achievements = TableReaderV2.Parse<StrongholdRewardTable>();
        var heads = TableReaderV2.Parse<AscNet.Table.V2.share.headportrait.HeadPortraitTable>();
        bool IsHead(AscNet.Table.V2.share.reward.RewardGoodsTable goods) =>
            rewardType.Invoke(null, [goods]) is AscNet.GameServer.Handlers.RewardType.HeadPortrait;
        var fixtures = achievements.Where(row => conditions[row.Condition].Type == 10131)
            .Select(row => (Achievement: row, Goods: rewardRows[row.RewardId].SubIds
                .Where(id => id > 0).Select(id => goodsRows[id]).ToList())).ToList();

        foreach (bool repairHead in new[] { false, true })
        {
            var fixture = fixtures.First(row => repairHead
                ? row.Goods.Any(goods => IsHead(goods) && goods.Count == 1 && heads.Any(head => head.Id == goods.TemplateId))
                : row.Goods.All(goods => rewardType.Invoke(null, [goods]) is AscNet.GameServer.Handlers.RewardType.Item));
            const uint begin = 1_788_021_239;
            long playerId = repairHead ? 48_972 : 48_971;
            Player player = CreateDrawCompatibilityPlayer(playerId);
            player.PlayerData.Level = TableReaderV2.Parse<StrongholdLevelTable>()
                .Single(row => row.Id == fixture.Achievement.LevelId).MinLevel;
            player.Stronghold.ActivityId = 79;
            player.Stronghold.BeginTime = begin;
            using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
                out RecordingMongoCollectionProxy<Player> playerSaves,
                out RecordingMongoCollectionProxy<Character> characterSaves,
                out RecordingMongoCollectionProxy<Inventory> inventorySaves);
            prepare.Invoke(null, [player, DateTimeOffset.FromUnixTimeSeconds(begin)]);
            player.Stronghold.RewardIds = achievements.Where(row => row.Id != fixture.Achievement.Id).Select(row => row.Id).ToList();
            player.Stronghold.ClaimedRewardIds = player.Stronghold.RewardIds.ToList();
            player.Stronghold.FinishGroupIds = [conditions[fixture.Achievement.Condition].Params[0]];
            DateTimeOffset expiry = DateTimeOffset.FromUnixTimeSeconds(begin
                + (long)TableReaderV2.Parse<StrongholdActivityTable>().Single(row => row.Id == 79).OneCycleSeconds);
            if (!repairHead)
            {
                string beforeFailure = Convert.ToHexString(player.ToBson());
                playerSaves.ThrowOnReplaceOne = true;
                try
                {
                    prepare.Invoke(null, [player, expiry]);
                    throw new InvalidDataException("Norman forced rollover save failure did not throw.");
                }
                catch (TargetInvocationException exception) when (exception.InnerException is MongoDB.Driver.MongoException)
                {
                }
                finally
                {
                    playerSaves.ThrowOnReplaceOne = false;
                }
                AssertEqual(beforeFailure, Convert.ToHexString(player.ToBson()),
                    "Norman failed rollover save leaves original cycle and mailbox unchanged");
            }
            prepare.Invoke(null, [player, expiry]);
            AssertEqual(80, player.Stronghold.ActivityId, "Norman rollover retry advances exactly one cycle");
            AssertEqual(2, player.Mails.Count, "Norman rollover retry creates one result and one eligible reward mail");
            string completedRollover = Convert.ToHexString(player.ToBson());
            prepare.Invoke(null, [player, expiry]);
            AssertEqual(completedRollover, Convert.ToHexString(player.ToBson()),
                "Norman rollover retry and subsequent login leave a stable cycle and mailbox");
            string claimKey = $"stronghold:{playerId}:79:{begin}:achievement:{fixture.Achievement.Id}";
            PlayerMail mail = player.Mails.Single(value => value.RewardClaimKey == claimKey);
            Character character = CreateDrawCompatibilityCharacter(playerId);
            Inventory inventory = CreateDrawCompatibilityInventory(playerId, fixture.Goods
                .Where(goods => rewardType.Invoke(null, [goods]) is AscNet.GameServer.Handlers.RewardType.Item)
                .GroupBy(goods => goods.TemplateId)
                .Select(group => new Item { Id = group.Key, Count = repairHead ? 17 : 17 + group.Sum(goods => goods.Count) }));
            if (!repairHead)
            {
                inventory.AppliedRewardClaims.Add(claimKey);
                character.AppliedRewardClaims.Add(claimKey);
            }
            using LoopbackSessionHarness harness = new(character, player, inventory, $"norman-mail-{repairHead}");
            int packetId = repairHead ? 48_980 : 48_975;
            MailGetSingleRewardResponse Claim(out List<string> pushes)
            {
                InvokeRegisteredRequestHandler(nameof(MailGetSingleRewardRequest), harness.Session, packetId,
                    new MailGetSingleRewardRequest { Id = mail.Id });
                return ReadStrongholdResponse<MailGetSingleRewardResponse>(harness, packetId++, out pushes);
            }
            string balancesBefore = string.Join(",", inventory.Items.OrderBy(item => item.Id).Select(item => $"{item.Id}:{item.Count}"));
            if (repairHead)
            {
                PlayerMailRewardGoods headGoods = mail.RewardGoodsList!.Single(goods =>
                    fixture.Goods.Any(row => IsHead(row) && row.TemplateId == goods.TemplateId));
                headGoods.Count = 2;
                string invalidPlayer = Convert.ToHexString(player.ToBson());
                string invalidInventory = Convert.ToHexString(inventory.ToBson());
                string invalidCharacter = Convert.ToHexString(character.ToBson());
                MailGetSingleRewardResponse invalid = Claim(out List<string> invalidPushes);
                AssertEqual(true, invalid.Code != 0, "Norman portrait mail rejects non-unit entitlement count");
                AssertEqual(0, invalidPushes.Count, "Norman invalid portrait mail emits no partial item reward");
                AssertEqual(invalidPlayer, Convert.ToHexString(player.ToBson()), "Norman invalid portrait mail preserves player state");
                AssertEqual(invalidInventory, Convert.ToHexString(inventory.ToBson()), "Norman invalid portrait mail preserves inventory");
                AssertEqual(invalidCharacter, Convert.ToHexString(character.ToBson()), "Norman invalid portrait mail preserves roster and receipts");
                headGoods.Count = 1;
                byte[] durablePlayer = playerSaves.LastSuccessfulReplacementBson
                    ?? throw new InvalidDataException("Norman mail fixture did not persist rollover.");
                playerSaves.ThrowOnReplaceOne = true;
                MailGetSingleRewardResponse failed = Claim(out List<string> failedPushes);
                playerSaves.ThrowOnReplaceOne = false;
                AssertEqual(true, failed.Code != 0, "Norman portrait mail reports player-save failure");
                AssertEqual(0, failedPushes.Count, "Norman portrait mail failure publishes no uncommitted entitlement");
                AssertEqual(0, mail.Status, "Norman portrait mail remains claimable after player-save failure");
                AssertEqual(false, player.HeadPortraits.Any(head => fixture.Goods.Any(goods => IsHead(goods) && goods.TemplateId == head.Id)),
                    "Norman portrait failed player save rolls back entitlement");
                harness.Session.player = BsonSerializer.Deserialize<Player>(durablePlayer);
                harness.Session.character = BsonSerializer.Deserialize<Character>(characterSaves.LastSuccessfulReplacementBson
                    ?? throw new InvalidDataException("Norman portrait failure did not reach character receipt persistence."));
                harness.Session.inventory = BsonSerializer.Deserialize<Inventory>(inventorySaves.LastSuccessfulReplacementBson
                    ?? throw new InvalidDataException("Norman portrait failure did not reach inventory persistence."));
                AssertEqual(true, harness.Session.character.AppliedRewardClaims.Contains(claimKey),
                    "Norman portrait failure reload retains durable character receipt");
                balancesBefore = string.Join(",", harness.Session.inventory.Items.OrderBy(item => item.Id).Select(item => $"{item.Id}:{item.Count}"));
                mail = harness.Session.player.Mails.Single(value => value.RewardClaimKey == claimKey);
            }

            MailGetSingleRewardResponse claimed = Claim(out List<string> pushes);
            AssertEqual(0, claimed.Code, $"Norman mail receipt replay succeeds, portrait={repairHead}");
            AssertEqual(3, claimed.Status, $"Norman mail receipt replay publishes claimed status, portrait={repairHead}");
            AssertEqual(balancesBefore, string.Join(",", harness.Session.inventory.Items.OrderBy(item => item.Id)
                .Select(item => $"{item.Id}:{item.Count}")), $"Norman mail original achievement receipt prevents duplicate inventory, portrait={repairHead}");
            Player persisted = BsonSerializer.Deserialize<Player>(playerSaves.LastSuccessfulReplacementBson
                ?? throw new InvalidDataException("Norman mail claim did not save player."));
            AssertEqual(3, persisted.Mails.Single(value => value.Id == mail.Id).Status,
                $"Norman mail claim status survives BSON reload, portrait={repairHead}");
            if (repairHead)
            {
                AssertEqual(true, pushes.Contains(nameof(NotifyHeadPortraitInfos)), "Norman portrait retry publishes repaired entitlement");
                AssertIntegerList(fixture.Goods.Where(IsHead).Select(goods => (long)goods.TemplateId).Order().ToArray(),
                    persisted.HeadPortraits.Select(head => head.Id).Order().ToArray(),
                    "Norman portrait receipt replay repairs exact missing player-owned entitlement");
            }
            string grantedPlayer = Convert.ToHexString(harness.Session.player.ToBson());
            MailGetSingleRewardResponse repeated = Claim(out List<string> repeatedPushes);
            AssertEqual(3, repeated.Status, $"Norman duplicate mail claim remains claimed, portrait={repairHead}");
            AssertEqual(0, repeatedPushes.Count, $"Norman duplicate mail claim emits no reward grant, portrait={repairHead}");
            AssertEqual(grantedPlayer, Convert.ToHexString(harness.Session.player.ToBson()),
                $"Norman duplicate mail claim does not duplicate player entitlements, portrait={repairHead}");
        }
    }

    private static void ValidateStrongholdHistoryRewardSchemaEdge()
    {
        // No current retail reward selects history mode. Exercise the valid fourth-parameter schema
        // with a temporary condition override, never a new runtime reward mapping.
        var conditions = TableReaderV2.Parse<ConditionTable>().ToDictionary(row => row.Id);
        var achievements = TableReaderV2.Parse<StrongholdRewardTable>();
        StrongholdRewardTable achievement = achievements.First(row => conditions[row.Condition].Type == 12103
            && conditions[row.Condition].Params.Count >= 3);
        ConditionTable condition = conditions[achievement.Condition];
        int[] originalParams = condition.Params.ToArray();
        Type module = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.StrongholdModule");
        MethodInfo prepare = RequiredMethod(module, "PrepareLogin", BindingFlags.Static | BindingFlags.NonPublic,
            [typeof(Player), typeof(DateTimeOffset)]);
        const uint begin = 1_788_021_239;
        DateTimeOffset expiry = DateTimeOffset.FromUnixTimeSeconds(begin
            + (long)TableReaderV2.Parse<StrongholdActivityTable>().Single(row => row.Id == 79).OneCycleSeconds);
        var reward = TableReaderV2.Parse<AscNet.Table.V2.share.reward.RewardTable>().Single(row => row.Id == achievement.RewardId);
        var goods = TableReaderV2.Parse<AscNet.Table.V2.share.reward.RewardGoodsTable>()
            .Where(row => reward.SubIds.Contains(row.Id)).ToList();
        string expectedGoods = string.Join(",", goods.GroupBy(row => row.TemplateId).OrderBy(group => group.Key)
            .Select(group => $"{group.Key}:{group.Sum(row => row.Count)}"));
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(out _, out _, out _);
        StrongholdFinishGroupInfo History(int energy) => new()
        {
            Id = originalParams[0],
            UsedElectricEnergy = originalParams[2] == 0 ? energy : 0,
            UsedSystemElectricEnergy = originalParams[2] != 0 ? energy : 0
        };
        Player Fresh(long uid)
        {
            Player player = CreateDrawCompatibilityPlayer(uid);
            player.PlayerData.Level = TableReaderV2.Parse<StrongholdLevelTable>()
                .Single(row => row.Id == achievement.LevelId).MinLevel;
            player.Stronghold.ActivityId = 79;
            player.Stronghold.BeginTime = begin;
            prepare.Invoke(null, [player, DateTimeOffset.FromUnixTimeSeconds(begin)]);
            player.Stronghold.RewardIds = achievements.Where(row => row.Id != achievement.Id).Select(row => row.Id).Order().ToList();
            player.Stronghold.ClaimedRewardIds = player.Stronghold.RewardIds.ToList();
            player.Stronghold.SettledPeriod = long.MaxValue; // this fixture's cycle is long past; daily settlement is covered by --stronghold-cycle-only
            player.Stronghold.HistoryFinishGroupInfos = [History(originalParams[1] + 1)];
            return player;
        }
        try
        {
            condition.Params.Clear();
            condition.Params.AddRange([originalParams[0], originalParams[1], originalParams[2], 1]);
            Player direct = Fresh(48_991);
            using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(48_991), direct,
                CreateDrawCompatibilityInventory(48_991, []), "norman-history-schema-claim");
            harness.Session.stage = CreateLoginAccountCompatibilityStage(48_991);
            int packetId = 48_993;
            GetStrongholdRewardResponse Claim(out List<string> pushes)
            {
                InvokeRegisteredRequestHandler(nameof(GetStrongholdRewardRequest), harness.Session, packetId,
                    new GetStrongholdRewardRequest { Ids = [achievement.Id] });
                return ReadStrongholdResponse<GetStrongholdRewardResponse>(harness, packetId++, out pushes);
            }
            string before = Convert.ToHexString(direct.ToBson());
            AssertEqual(20113018, Claim(out List<string> rejectedPushes).Code,
                "Norman hypothetical history reward rejects older over-threshold record alone");
            AssertEqual(0, rejectedPushes.Count, "Norman rejected history reward emits no grant");
            AssertEqual(before, Convert.ToHexString(direct.ToBson()), "Norman rejected history reward preserves state and claims");
            direct.Stronghold.HistoryFinishGroupInfos.Add(History(originalParams[1]));
            GetStrongholdRewardResponse success = Claim(out _);
            AssertEqual(0, success.Code, "Norman hypothetical history reward accepts later qualifying record after older failure");
            AssertIntegerList([achievement.Id], success.SuccessIds.Select(id => (long)id).ToArray(),
                "Norman hypothetical history reward returns requested achievement");
            AssertEqual(expectedGoods, string.Join(",", success.RewardGoodsList.GroupBy(row => row.TemplateId).OrderBy(group => group.Key)
                .Select(group => $"{group.Key}:{group.Sum(row => row.Count)}")), "Norman history claim returns exact authoritative goods");
            string grantedInventory = Convert.ToHexString(harness.Session.inventory.ToBson());
            string grantedPlayer = Convert.ToHexString(direct.ToBson());
            AssertEqual(20113018, Claim(out _).Code, "Norman history reward retry rejects lifetime duplicate");
            AssertEqual(grantedInventory, Convert.ToHexString(harness.Session.inventory.ToBson()), "Norman history retry cannot duplicate inventory");
            AssertEqual(grantedPlayer, Convert.ToHexString(direct.ToBson()), "Norman history retry preserves lifetime claims");

            Player onlyOld = Fresh(48_992);
            prepare.Invoke(null, [onlyOld, expiry]);
            AssertEqual(1, onlyOld.Mails.Count, "Norman history-only over-threshold rollover sends result mail only");
            AssertEqual(false, onlyOld.Stronghold.ClaimedRewardIds.Contains(achievement.Id),
                "Norman history-only over-threshold rollover does not claim reward");
            Player settlement = Fresh(48_996);
            settlement.Stronghold.HistoryFinishGroupInfos.Add(History(originalParams[1]));
            prepare.Invoke(null, [settlement, expiry]);
            string key = $"stronghold:{settlement.PlayerData.Id}:79:{begin}:achievement:{achievement.Id}";
            PlayerMail mail = settlement.Mails.Single(value => value.RewardClaimKey == key);
            AssertEqual(2, settlement.Mails.Count, "Norman later qualifying history yields achievement and result mails");
            AssertEqual(expectedGoods, string.Join(",", mail.RewardGoodsList!.GroupBy(row => row.TemplateId).OrderBy(group => group.Key)
                .Select(group => $"{group.Key}:{group.Sum(row => row.Count)}")), "Norman history settlement contains exact authoritative goods");
            AssertEqual(true, settlement.Stronghold.RewardIds.Count == 0 && settlement.Stronghold.ClaimedRewardIds.Count == 0,
                "Norman history settlement leaves the new cycle with no claims (claims are per cycle)");
            Player reloaded = BsonSerializer.Deserialize<Player>(settlement.ToBson());
            string settled = Convert.ToHexString(reloaded.ToBson());
            prepare.Invoke(null, [reloaded, expiry]);
            AssertEqual(settled, Convert.ToHexString(reloaded.ToBson()), "Norman history settlement BSON retry preserves claims and avoids duplicate mail");
        }
        finally
        {
            condition.Params.Clear();
            condition.Params.AddRange(originalParams);
        }
    }
}
