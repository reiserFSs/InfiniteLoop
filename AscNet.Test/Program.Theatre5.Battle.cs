using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.dlcworld;
using AscNet.Table.V2.share.theatre5;
using MessagePack;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Newtonsoft.Json.Linq;
using System.Reflection;

namespace AscNet.Test;

internal partial class Program
{
    // SYNTHETIC SERVER-BOUNDARY INPUT ONLY. The report world is derived from the frozen
    // entry response the way the client derives it, not from an independently executed
    // StatusSyncFight result. These checks prove transport, authorization and durable
    // outcomes, never combat.
    private static DlcSingleFightSettleRequest GodfallSyntheticNativeResult(GodfallCase test, bool win = true, bool bothDead = false)
    {
        var attempt = GodfallAttempt(test);
        // Explicit synthetic elapsed-time fixture, not a real ten-second fight.
        // No economic/story state or production clock is changed.
        attempt.StartedAt = Math.Min(attempt.StartedAt, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 10000);
        test.SaveFixture();
        var world = GodfallProjectedWorld(attempt);
        int owner = checked((int)test.Player.PlayerData.Id);
        var actors = world.AutoChessGameplayData!;
        // The installed client writes these additional zero-valued keys independently
        // of our projection; retaining them catches a regression to the old 105-key schema.
        foreach (var actor in new[] { actors.SelfData!, actors.EnemyData! })
            for (int id = 105; id < 148; id++)
                actor.AutoChessData!.Attribs.TryAdd(id, 0);
        return new()
        {
            DlcReportWorldResult = new()
            {
                DlcFightSettleData = new()
                {
                    WorldData = world, IsPlayerWin = win && !bothDead, FinishTime = 10,
                    // The native single-player producer leaves shared multiplayer
                    // ChapterId/timestamps/outer RoomId/FightUid unset.
                    PlayerData = new() { [owner] = new() { PlayerId = owner, IsWin = win && !bothDead } },
                    AutoChessCheckData = new()
                    {
                        ActBattleTime = 5000,
                        MyData = new() { RecordData = new() }, EnemyData = new() { RecordData = new() }
                    },
                    // Native may omit removed dead actors and include fatigue NPCs;
                    // this is one admissible snapshot, not a required two-NPC rule.
                    NpcSettleInfos = new()
                    {
                        [1] = new() { NpcId = 1, TemplateId = actors.SelfData!.TemplateId, IsPlayer = true, LeftHp = win && !bothDead ? 1 : 0 },
                        [2] = new() { NpcId = 2, TemplateId = actors.EnemyData!.TemplateId, LeftHp = win || bothDead ? 0 : 1 }
                    }
                }
            }
        };
    }

    // Rebuilds the native world the client derives from the frozen entry response and
    // effects. The handler's projection supplies the fields unrelated to the native int
    // boundary; the conversion-valued attributes are set independently by
    // GodfallCheckNativeAttribConversion, so a conversion regression cannot hide behind
    // a fixture copied from the handler's own output.
    private static Theatre5WorldData GodfallProjectedWorld(Theatre5Attempt attempt)
    {
        var entry = MessagePackSerializer.Deserialize<DlcSingleEnterFightResponse>(attempt.EntryResponse).WorldData!;
        return (Theatre5WorldData)RequiredMethod(RequiredAscNetGameServerType("AscNet.GameServer.Handlers.Theatre5Module"),
            "ProjectNativeWorld", BindingFlags.Static | BindingFlags.NonPublic,
            [typeof(Theatre5WorldData), typeof(List<Theatre5Effect>)]).Invoke(null, [entry, attempt.Effects])!;
    }

    private static Theatre5Attempt GodfallAttempt(GodfallCase test) =>
        (test.Data.PvpType == 1 ? test.State.PvpAttempt : test.State.PveAttempt)
        ?? throw new InvalidOperationException("Godfall fixture has no authorized native attempt.");

    private static DlcSingleFightSettleRequest GodfallMinimalResult(GodfallCase test, int state)
    {
        int owner = checked((int)test.Player.PlayerData.Id);
        return new()
        {
            DlcReportWorldResult = new()
            {
                DlcFightSettleData = new()
                {
                    SettleState = state, WorldData = new() { WorldId = 200, Players = [new() { Id = owner }] },
                    PlayerData = new() { [owner] = new() { PlayerId = owner } }
                }
            }
        };
    }

    private static void GodfallBattleFixture(GodfallCase test, int variant = 0)
    {
        test.Start();
        // Explicit persisted branch fixture; economy/story traversal is checked elsewhere.
        var character = TableReaderV2.Parse<Theatre5CharacterTable>().Where(row => row.Priority > 0).OrderBy(row => row.Id).ElementAt(variant);
        var adventure = test.Adventure;
        adventure.CharacterId = character.Id;
        adventure.CharacterLv = variant + 1;
        adventure.CharacterExp = 0;
        adventure.Status = 4;
        adventure.SkillChoiceData = null;
        adventure.RandomRelics.Clear();
        adventure.ChooseMissions.Clear();
        adventure.Missioning = null;
        adventure.IsCanFreeUnlockGrid = false;
        adventure.BagData.TempItemDict.Clear();
        var rune = TableReaderV2.Parse<Theatre5ItemRuneTable>().Where(row => Convert.ToInt32(row.RuneAttrId) > 0).OrderBy(row => row.Id).ElementAt(variant);
        adventure.BagData.RuneDict = new() { [1] = new() { InstanceId = ++adventure.IdSequence, ItemId = rune.Id, ItemType = 2 } };
        test.Data.Characters[character.Id] = new()
        {
            Id = character.Id, FashionId = character.FashionIds[0], Rating = ((Theatre5PvpAdventureData)adventure).NormalOriginRating
        };
        test.Players.FindResults = [];
        test.SaveFixture();
    }

    private static JObject GodfallEnterNative(GodfallCase test)
    {
        if (test.Data.PvpType == 1 && test.Adventure.Status == 4)
            test.Success(nameof(Theatre5MatchRequest), new Theatre5MatchRequest());
        return test.Success(nameof(DlcSingleEnterFightRequest), new DlcSingleEnterFightRequest
        {
            WorldId = 200, LevelId = test.Data.PvpType == 2 ? test.Data.PveAdventureData!.PveChapterData!.CurPveChapterLevel!.Level : null
        });
    }

    private static void GodfallPrepareBattle(GodfallCase test)
    {
        // Drive only earned/pending choices through registered RPCs; never fund or
        // rewrite a natural story/PvP run to make it battleable.
        for (int step = 0; step < 256; step++)
        {
            var adventure = test.Adventure;
            var bag = adventure.BagData;
            if (adventure.SkillChoiceData is { } skills)
            {
                int slot = Enumerable.Range(1, bag.SkillGridsNum).FirstOrDefault(i => !bag.SkillDict.ContainsKey(i));
                test.Success(nameof(XTheatre5SkillChoiceRequest), new XTheatre5SkillChoiceRequest
                { InstanceId = skills.SkillGroups[0].InstanceId, IsEquipped = slot > 0, TargetIndex = slot > 0 ? slot : -1 });
                continue;
            }
            if (adventure.RandomRelics.Count > 0)
            {
                test.Success(nameof(XTheatre5RelicChooseRequest), new XTheatre5RelicChooseRequest { InstanceId = adventure.RandomRelics[0].InstanceId });
                continue;
            }
            int group = test.Data.PvpType == 1
                ? Convert.ToInt32(TableReaderV2.Parse<Theatre5ConfigTable>().Single(row => row.Key == "PvpLevelGroup").Values[0])
                : TableReaderV2.Parse<AscNet.Table.V2.share.theatre5.theatre5pve.Theatre5PveChapterTable>()
                    .Single(row => row.Id == test.Data.PveAdventureData!.PveChapterData!.ChapterId).LevelGroup;
            var levels = TableReaderV2.Parse<Theatre5CharacterLevelTable>().Where(row => row.GroupId == group).ToList();
            var level = levels.Single(row => (row.Level ?? 0) == adventure.CharacterLv);
            if (level.Exp > 0 && adventure.CharacterExp >= level.Exp && levels.Any(row => row.Level == adventure.CharacterLv + 1))
            {
                test.Success(nameof(XTheatre5CharacterLevelUpRequest), new XTheatre5CharacterLevelUpRequest());
                continue;
            }
            if (adventure.IsCanFreeUnlockGrid)
            {
                test.Success(nameof(XTheatre5ShopUnlockGridRequest), new XTheatre5ShopUnlockGridRequest { GridType = 2 });
                continue;
            }
            if (adventure.ChooseMissions.Count > 0)
            {
                test.Success(nameof(Theatre5MissionChooseRequest), new Theatre5MissionChooseRequest { PositionId = adventure.ChooseMissions.Keys.Min() });
                continue;
            }
            if (adventure.Missioning is { MissionState: 2 } mission)
            {
                var bounty = TableReaderV2.Parse<AscNet.Table.V2.share.theatre5.theatremission.Theatre5MissionBountyTable>()
                    .Single(row => row.Bounty == mission.MissionBounty.Bounty && row.Level == mission.MissionBounty.BountyLevel);
                test.Success(nameof(Theatre5MissionRewardRequest), new Theatre5MissionRewardRequest { ChooseItemId = bounty.BountyItem.First(id => id > 0) });
                continue;
            }
            if (test.Data.PvpType == 2 && test.Data.PveAdventureData!.ItemBoxSelectData.FirstOrDefault() is { } choice)
            {
                test.Success(nameof(XTheatre5ItemBoxSelectRequest), new XTheatre5ItemBoxSelectRequest
                { BoxInstanceId = choice.BoxInstanceId, ItemInstanceId = choice.ItemList[0].InstanceId });
                continue;
            }
            var box = bag.BagItemDict.Values.Concat(bag.TempItemDict.Values).FirstOrDefault(item => item.ItemType == 3);
            if (test.Data.PvpType == 2 && box != null)
            {
                test.Success(nameof(XTheatre5ItemBoxOpenRequest), new XTheatre5ItemBoxOpenRequest { BoxInstanceId = box.InstanceId });
                continue;
            }
            if (bag.TempItemDict.Count > 0)
            {
                var item = bag.TempItemDict.OrderBy(pair => pair.Key).First();
                int slot = Enumerable.Range(1, bag.BagGridsNum).FirstOrDefault(i => !bag.BagItemDict.ContainsKey(i));
                if (slot == 0)
                {
                    var sell = bag.BagItemDict.Values.First(value => value.ItemType is 1 or 2 or 6);
                    test.Success(nameof(XTheatre5ShopSellItemRequest), new XTheatre5ShopSellItemRequest
                    { InstanceId = sell.InstanceId, ItemType = sell.ItemType, IsEquipped = false });
                }
                else test.Success(nameof(XTheatre5BagItemMoveRequest), new XTheatre5BagItemMoveRequest
                {
                    InstanceId = item.Value.InstanceId, ItemType = item.Value.ItemType, SrcIndex = item.Key,
                    SrcIsTempItem = true, TargetIndex = slot
                });
                continue;
            }
            Require(adventure.Status == 4, "Natural interrupt driver reaches shopping before native battle");
            return;
        }
        throw new InvalidDataException("Godfall pending choices did not converge to a battleable shop.");
    }

    private static void GodfallCheckCompletePvpRuns()
    {
        foreach (bool extra in new[] { false, true })
        {
            using GodfallCase test = new($"natural-pvp-{extra}");
            test.Players.FindResults = [];
            test.Start();
            int target = Convert.ToInt32(TableReaderV2.Parse<Theatre5ConfigTable>()
                .Single(row => row.Key == (extra ? "PvpExtraTarget" : "PvpTarget")).Values[0]);
            int character = test.Adventure.CharacterId;
            int initialRating = test.Data.Characters[character].Rating;
            int normalTarget = Convert.ToInt32(TableReaderV2.Parse<Theatre5ConfigTable>().Single(row => row.Key == "PvpTarget").Values[0]);
            for (int win = 1; win <= target; win++)
            {
                GodfallPrepareBattle(test);
                GodfallEnterNative(test);
                double beforeProcess = test.State.PvpProcessRating;
                int opponentRating = test.State.PvpOpponentRating;
                var report = GodfallSyntheticNativeResult(test);
                JObject response = test.Success(nameof(DlcSingleFightSettleRequest), report);
                var result = response["DlcFightSettleData"]!["XAutoChessGameplayResult"]!;
                Require((int)result["TrophyNum"]! == win, "Natural shop/match progression preserves every synthetic winning round");
                if (extra && win == normalTarget + 1)
                {
                    var rank = TableReaderV2.Parse<Theatre5RankTable>()
                        .Where(row => Convert.ToInt32(row.Rating) <= initialRating).MaxBy(row => row.Id)!;
                    double actual = Convert.ToInt32(TableReaderV2.Parse<Theatre5ConfigTable>().Single(row => row.Key == "WWin").Values[0]) / 10000d;
                    double expected = 1d / (1d + Math.Pow(10d, Math.Clamp((opponentRating - initialRating) / 400d, -8d, 8d)));
                    Require(Math.Abs((double)result["ProcessRating"]! - beforeProcess - rank.KExWin * (actual - expected)) < 1e-9,
                        "First overtime round uses the original rating baseline, not the normal-finish preview, in the approved local Elo rule");
                }
                if ((bool)result["IsPvpExtraChoice"]!)
                {
                    int preview = (int)result["NormalOriginRating"]!;
                    int protection = test.Data.Characters[character].RankProtectNum;
                    byte[] inventory = test.Session.inventory.ToBson();
                    var granted = test.State.ClaimedRewards.ToHashSet();
                    Require(preview == ((Theatre5PvpAdventureData)test.Adventure).NormalOriginRating
                        && test.Data.Characters[character].Rating == initialRating,
                        "Status8 exposes projected normal rating without prematurely changing the character rating");
                    test.Reload();
                    Require(test.Adventure.Status == 8 && test.Login().PvpAdventureData!.NormalOriginRating == preview
                        && test.Login().PvpAdventureData!.NormalOriginRating == preview,
                        "Normal-finish preview is stable across status8 relog and repeated login hydration");
                    JObject replay = test.Success(nameof(DlcSingleFightSettleRequest), report);
                    Require(JToken.DeepEquals(response, replay) && test.Data.Characters[character].Rating == initialRating
                        && test.Data.Characters[character].RankProtectNum == protection
                        && inventory.SequenceEqual(test.Session.inventory.ToBson()) && granted.SetEquals(test.State.ClaimedRewards),
                        "Repeated normal preview/replay consumes no rank protection, grants, inventory or character rating");
                    JObject choice = test.Success(nameof(Theatre5SettleExtraChoiceRequest), new Theatre5SettleExtraChoiceRequest { IsExtra = extra });
                    if (!extra)
                    {
                        Require((bool)choice["SettleResult"]!["IsFinish"]!, "Declining overtime finalizes normal PvP");
                        Require((int)choice["SettleResult"]!["Rating"]! == preview && test.Data.Characters[character].Rating == preview,
                            "Declined overtime commits exactly the displayed normal-finish rating after relog");
                        break;
                    }
                }
                if (win == target)
                    Require((bool)result["IsFinish"]!, "Winning authored overtime target finalizes extended PvP");
                else test.Success(nameof(Theatre5EnterShopRequest), new Theatre5EnterShopRequest());
            }
            int rating = test.Data.Characters[character].Rating;
            test.Reload();
            Require(test.Data.Characters[character].Rating == rating && test.Data.CommonFightCnt[character] == target,
                "Completed natural PvP run persists rating and exactly-once battle count");
        }
    }

    private static void RunGodfallBattleChecks()
    {
        // Width/schema round-trip uses nontrivial values without probabilistic RNG assertions.
        var wide = new Theatre5WorldData { WorldId = 200, RoomId = "theatre5:wide-seed", ServerControllerSeed = 0x71234567 };
        var roundtrip = MessagePackSerializer.Deserialize<Theatre5WorldData>(MessagePackSerializer.Serialize(wide));
        Require(roundtrip.ServerControllerSeed == wide.ServerControllerSeed && roundtrip.RoomId == wide.RoomId,
            "Native RoomId string and full int32 seed survive MessagePack");
        var rating = MessagePackSerializer.Deserialize<Theatre5AutoChessGameplayResult>(MessagePackSerializer.Serialize(
            new Theatre5AutoChessGameplayResult { ProcessRating = -12.375 }));
        Require(rating.ProcessRating == -12.375, "Native ProcessRating retains a fractional double");

        for (int variant = 0; variant < 2; variant++)
        {
            using GodfallCase test = new($"native-schema-{variant}");
            GodfallBattleFixture(test, variant);
            JObject entry = GodfallEnterNative(test);
            JObject world = (JObject)entry["WorldData"]!;
            var attempt = GodfallAttempt(test);
            Require((int)world["WorldId"]! == 200 && (int)world["WorldType"]! == 5 && (int)world["RebootId"]! == 1,
                "Godfall enters authored DLC world rather than ordinary Fight");
            Require(world["RoomId"]!.Type == JTokenType.String && (string)world["RoomId"]! == attempt.RoomId
                && (int)world["ServerControllerSeed"]! == attempt.Seed, "Entry identity is the durable attempt identity");
            Require((int)world["LevelId"]! is >= 1071 and <= 1076, "Native level belongs to authored world200");
            foreach (string side in new[] { "SelfData", "EnemyData" })
            {
                var actor = world["AutoChessGameplayData"]![side]!;
                var data = actor["AutoChessData"]!;
                var character = TableReaderV2.Parse<Theatre5CharacterTable>().Single(row => row.Id == (int)data["CharacterId"]!);
                Require((int)actor["TemplateId"]! == character.TemplateId && data["Attribs"] is JObject && data["MagicIds"] is JObject,
                    "Both mandatory actors expose native template identity and writable attribute/magic maps");
            }
            var self = world["AutoChessGameplayData"]!["SelfData"]!["AutoChessData"]!;
            Require((int)self["CharacterId"]! == test.Adventure.CharacterId && (int)self["CharacterLevel"]! == variant + 1
                && (int)self["RuneEvolves"]![0]!["RuneId"]! == test.Adventure.BagData.RuneDict[1].ItemId,
                "Distinct persisted character, level and rune inputs reach native entry without fabricated final stats");
            byte[] frozen = attempt.EntryResponse.ToArray();
            test.Reload();
            JObject cached = GodfallEnterNative(test);
            Require(JToken.DeepEquals(entry, cached) && GodfallAttempt(test).EntryResponse.SequenceEqual(frozen),
                "Lost entry response/relog replays the same native authorization");
            if (variant == 0) GodfallCheckCrossModeAttempt(test);

            int health = test.Adventure.Health, round = test.Adventure.RoundNum;
            int discountRounds = test.Adventure.BagData.RoundNumWithoutGridUnlock;
            int trophies = ((Theatre5PvpAdventureData)test.Adventure).TrophyNum;
            var request = GodfallSyntheticNativeResult(test, win: variant == 0);
            JObject settled = test.Success(nameof(DlcSingleFightSettleRequest), request);
            Require(test.Adventure.BagData.RoundNumWithoutGridUnlock == discountRounds + 1,
                "Resolved normal win/loss advances the unbought-grid discount by exactly one round");
            Require(test.Adventure.RoundNum == round + 1 && test.Adventure.Health == health - variant
                && ((Theatre5PvpAdventureData)test.Adventure).TrophyNum == trophies + (variant == 0 ? 1 : 0),
                "Synthetic authorized win/loss consumes one round and the correct health/trophy outcome");
            Require(test.Adventure.IsCanFreeUnlockGrid == (variant == 0)
                && (bool)settled["DlcFightSettleData"]!["XAutoChessGameplayResult"]!["IsCanFreeUnlockGrid"]! == (variant == 0),
                "Only a resolved victory below rune capacity grants the next free-grid interrupt");
            int count = test.Data.CommonFightCnt[test.Adventure.CharacterId];
            test.Reload();
            JObject replay = test.Success(nameof(DlcSingleFightSettleRequest), request);
            Require(JToken.DeepEquals(settled, replay) && test.Data.CommonFightCnt[test.Adventure.CharacterId] == count,
                "Lost settlement response/relog semantic retry returns cached result without duplicate progress");
            Require(test.Adventure.BagData.RoundNumWithoutGridUnlock == discountRounds + 1,
                "Settlement replay/relog does not accrue a second grid-discount round");
            var changed = MessagePackSerializer.Deserialize<DlcSingleFightSettleRequest>(MessagePackSerializer.Serialize(request));
            changed.DlcReportWorldResult.DlcFightSettleData!.IsPlayerWin = !request.DlcReportWorldResult.DlcFightSettleData!.IsPlayerWin;
            test.Error(nameof(DlcSingleFightSettleRequest), changed, 20280008);
        }

        using (GodfallCase test = new("native-both-dead-loss"))
        {
            GodfallBattleFixture(test);
            GodfallEnterNative(test);
            int health = test.Adventure.Health;
            test.Success(nameof(DlcSingleFightSettleRequest), GodfallSyntheticNativeResult(test, bothDead: true));
            Require(test.Adventure.Health == health - 1 && test.State.PvpLoseCount == 1 && test.State.PvpDrawCount == 0,
                "Native self-death-first both-dead result is a loss, never an invented draw");
        }
        using (GodfallCase test = new("native-win-at-rune-cap"))
        {
            GodfallBattleFixture(test);
            test.Adventure.BagData.RuneGridsNum = Convert.ToInt32(TableReaderV2.Parse<Theatre5ConfigTable>()
                .Single(row => row.Key == "RuneGridMaxNum").Values[0]);
            test.SaveFixture();
            GodfallEnterNative(test);
            JObject result = test.Success(nameof(DlcSingleFightSettleRequest), GodfallSyntheticNativeResult(test));
            Require(!test.Adventure.IsCanFreeUnlockGrid
                && !(bool)result["DlcFightSettleData"]!["XAutoChessGameplayResult"]!["IsCanFreeUnlockGrid"]!,
                "Victory at authored rune capacity cannot create an impossible free-grid interrupt");
        }
        GodfallCheckReportFailures();
        GodfallCheckNativeAttribConversion();
        GodfallCheckSettlementPersistenceFailures();
        GodfallCheckInterruptions();
        GodfallCheckOpponentSnapshots();
        GodfallCheckBattleEffects();
        GodfallCheckCompletePvpRuns();
        Console.WriteLine("Godfall battle proof: synthetic native-format server transport/state boundaries; no StatusSyncFight execution or native combat verification.");
    }

    private static void GodfallCheckSettlementPersistenceFailures()
    {
        foreach (bool committedIntent in new[] { false, true })
        {
            using GodfallCase test = new($"native-save-failure-{committedIntent}");
            GodfallBattleFixture(test);
            GodfallEnterNative(test);
            var request = GodfallSyntheticNativeResult(test);
            int round = test.Adventure.RoundNum, health = test.Adventure.Health;
            int count = test.Data.CommonFightCnt.GetValueOrDefault(test.Adventure.CharacterId);
            int saves = 0;
            test.Players.BeforeReplaceOne = _ =>
            {
                if (++saves == (committedIntent ? 2 : 1))
                    throw new MongoDB.Driver.MongoException("Godfall synthetic settlement persistence fault");
            };
            try { test.Call(nameof(DlcSingleFightSettleRequest), request, success: false); }
            finally { test.Players.BeforeReplaceOne = null; }
            Require(test.Adventure.RoundNum == round && test.Adventure.Health == health && test.Pushes.Count == 0,
                "Failed battle commit neither publishes progress nor acknowledges mutation pushes");
            Require((test.State.PendingMutation != null) == committedIntent,
                "Only a successfully persisted battle intent is recoverable");
            test.Relog("settlement persistence fault", pending: committedIntent);
            test.Success(nameof(DlcSingleFightSettleRequest), request);
            Require(test.Adventure.RoundNum == round + 1 && test.State.PendingMutation == null
                && test.Data.CommonFightCnt[test.Adventure.CharacterId] == count + 1,
                "Settlement retry recovers one durable battle outcome after either save boundary");
            test.Success(nameof(DlcSingleFightSettleRequest), request);
            Require(test.Data.CommonFightCnt[test.Adventure.CharacterId] == count + 1,
                "Recovered settlement response is replayable without duplicate progress");
        }
    }

    // The shipped client stores each projected double into its native Dictionary<int,int>
    // through the xLua int caster (Lua 5.3 lua_tointegerx with the managed int32 low
    // bits), so the fractional IdleSpinningSpeed/RunSpinningSpeed sources of this known
    // fixture reach Attribs 42/43 as 0 instead of a truncated 1. Setting those evidenced
    // values independently of the handler's projection makes the old truncating
    // expectation reject the report, the fixed one accept and progress it, and the
    // truncated value still fail as tampered.
    private static void GodfallCheckNativeAttribConversion()
    {
        using (GodfallCase test = new("native-attrib-conversion"))
        {
            GodfallBattleFixture(test);
            GodfallEnterNative(test);
            var attempt = GodfallAttempt(test);
            var request = GodfallSyntheticNativeResult(test);
            var actors = request.DlcReportWorldResult.DlcFightSettleData!.WorldData!.AutoChessGameplayData!;
            var character = TableReaderV2.Parse<Theatre5CharacterTable>().Single(row => row.Id == actors.SelfData!.AutoChessData!.CharacterId);
            var baseRow = TableReaderV2.Parse<DlcWorldAttribTable>().Single(row => row.Id == character.AttrId);
            Require(Convert.ToInt32(baseRow.IdleSpinningSpeed) % 10000 != 0 && Convert.ToInt32(baseRow.RunSpinningSpeed) % 10000 != 0
                && attempt.Effects.All(effect => effect.Type != 9 || effect.AddAttrResult is not { AttrType: 42 or 43 }),
                "Known fixture keeps both spinning-speed sources fractional with no temporary attribute altering them");
            actors.SelfData!.AutoChessData!.Attribs[42] = 0;
            actors.SelfData.AutoChessData.Attribs[43] = 0;
            int round = test.Adventure.RoundNum;
            test.Reload();
            JObject response = test.Success(nameof(DlcSingleFightSettleRequest), request);
            var echoed = MessagePackSerializer.Deserialize<DlcSingleFightSettleResponse>(test.LastResponseContent)
                .DlcFightSettleData!.ResultData.WorldData!.AutoChessGameplayData!.SelfData!.AutoChessData!.Attribs;
            Require((int)response["DlcFightSettleData"]!["XAutoChessGameplayResult"]!["CheckFailTimes"]! == 0
                && (bool)response["DlcFightSettleData"]!["ResultData"]!["IsPlayerWin"]! && test.Adventure.RoundNum == round + 1
                && echoed[42] == 0 && echoed[43] == 0 && echoed[0] == Convert.ToInt32(baseRow.Life),
                "Persisted synthetic native-format report with fractional attributes at 0 is accepted, progressed and echoes an unchanged integral Life");
        }
        using (GodfallCase test = new("native-attrib-conversion-tamper"))
        {
            GodfallBattleFixture(test);
            GodfallEnterNative(test);
            var request = GodfallSyntheticNativeResult(test);
            // The value the truncating expectation demanded is exactly the tamper.
            request.DlcReportWorldResult.DlcFightSettleData!.WorldData!.AutoChessGameplayData!.SelfData!.AutoChessData!.Attribs[42] = 1;
            JObject response = test.Success(nameof(DlcSingleFightSettleRequest), request);
            Require((int)response["DlcFightSettleData"]!["XAutoChessGameplayResult"]!["CheckFailTimes"]! == 1
                && !(bool)response["DlcFightSettleData"]!["ResultData"]!["IsPlayerWin"]!,
                "A report that keeps the truncated 1 for a fractional attribute remains a checked failure");
        }
        using (GodfallCase test = new("native-attrib-legacy-document"))
        {
            GodfallBattleFixture(test);
            GodfallEnterNative(test);
            var attempt = GodfallAttempt(test);
            var request = GodfallSyntheticNativeResult(test);
            // A pre-fix durable document still carries the removed projection element; its
            // truncated ints must be ignored and the attempt rebuilt from the frozen entry
            // response instead.
            var legacyWorld = GodfallProjectedWorld(attempt);
            foreach (var actor in new[] { legacyWorld.AutoChessGameplayData!.SelfData!, legacyWorld.AutoChessGameplayData.EnemyData! })
                actor.AutoChessData!.Attribs[42] = 1;
            BsonDocument legacy = test.State.ToBsonDocument();
            legacy[test.Data.PvpType == 1 ? nameof(PlayerTheatre5State.PvpAttempt) : nameof(PlayerTheatre5State.PveAttempt)]
                .AsBsonDocument["NativeWorld"] = new BsonBinaryData(MessagePackSerializer.Serialize(legacyWorld));
            test.Player.Theatre5 = BsonSerializer.Deserialize<PlayerTheatre5State>(legacy.ToBson());
            JObject response = test.Success(nameof(DlcSingleFightSettleRequest), request);
            Require((int)response["DlcFightSettleData"]!["XAutoChessGameplayResult"]!["CheckFailTimes"]! == 0
                && (bool)response["DlcFightSettleData"]!["ResultData"]!["IsPlayerWin"]!,
                "A legacy durable projection is ignored in favour of the frozen entry response");
        }
    }

    private static void GodfallCheckReportFailures()
    {
        using GodfallCase test = new("native-report-trust");
        GodfallBattleFixture(test);
        GodfallEnterNative(test);
        var foreign = GodfallSyntheticNativeResult(test);
        foreign.DlcReportWorldResult.DlcFightSettleData!.WorldData!.RoomId += ":stale";
        test.Error(nameof(DlcSingleFightSettleRequest), foreign, 20280017);
        Require(test.Adventure.CheckFailTimes == 0 && !GodfallAttempt(test).Settled, "Foreign identity does not spend a check failure or consume authorization");
        foreign = GodfallSyntheticNativeResult(test);
        foreign.DlcReportWorldResult.DlcFightSettleData!.PlayerData.Clear();
        test.Error(nameof(DlcSingleFightSettleRequest), foreign, 20280017);
        Require(test.Adventure.CheckFailTimes == 0, "Malformed owner map is a refusal, not a checked battle failure");
        int limit = Convert.ToInt32(TableReaderV2.Parse<Theatre5ConfigTable>().Single(row => row.Key == "BattleCheckFailTimesLimit").Values[0]);
        int health = test.Adventure.Health, round = test.Adventure.RoundNum;
        int discountRounds = test.Adventure.BagData.RoundNumWithoutGridUnlock;
        for (int fail = 1; fail <= limit; fail++)
        {
            var malformed = GodfallSyntheticNativeResult(test);
            var report = malformed.DlcReportWorldResult.DlcFightSettleData!;
            if (fail % 3 == 1) report.WorldData!.AutoChessGameplayData!.EnemyData = null;
            else if (fail % 3 == 2) report.AutoChessCheckData!.MyData!.TotalDamage = 1;
            else report.AutoChessCheckData!.ActBattleTime = 0;
            JObject response = test.Success(nameof(DlcSingleFightSettleRequest), malformed);
            Require((int)response["DlcFightSettleData"]!["XAutoChessGameplayResult"]!["CheckFailTimes"]! == fail,
                "Authorized malformed actor/record/time report increments checked failure once");
            test.Reload();
            JObject replay = test.Success(nameof(DlcSingleFightSettleRequest), malformed);
            Require(JToken.DeepEquals(response, replay), "A checked-failure lost response replays instead of charging another failure");
            if (fail < limit)
            {
                Require(test.Adventure.Health == health && test.Adventure.RoundNum == round,
                    "Below-limit check failure does not award a loss or advance a round");
                Require(test.Adventure.BagData.RoundNumWithoutGridUnlock == discountRounds,
                    "Checked-failure attempts/replays do not accrue resolved-round grid discounts");
                byte[] enemy = GodfallAttempt(test).OpponentSnapshot.ToArray();
                string room = GodfallAttempt(test).RoomId;
                GodfallEnterNative(test);
                Require(GodfallAttempt(test).RoomId != room && GodfallAttempt(test).OpponentSnapshot.SequenceEqual(enemy),
                    "Retry grants fresh room identity but preserves the frozen opponent");
            }
            else Require((bool)response["DlcFightSettleData"]!["XAutoChessGameplayResult"]!["IsFinish"]!,
                "Configured checked-failure limit finalizes the run rather than trapping the client");
        }
    }

    private static void GodfallCheckPveNerfLevels(GodfallCase source)
    {
        Require(source.Data.PvpType == 2 && source.Adventure.Status == 6,
            "PvE nerf branch fixtures originate from a naturally authorized chapter encounter");
        byte[] baseline = source.State.ToBson();
        int maxHealth = Convert.ToInt32(TableReaderV2.Parse<Theatre5ConfigTable>().Single(row => row.Key == "PveHealth").Values[0]);
        foreach (var branch in new (int Lost, int Streak, int Nerf)[] { (0, 0, 0), (2, 0, 2), (2, 1, 1), (2, 2, 0), (2, 3, 0) })
        {
            using GodfallCase test = new($"pve-nerf-{branch.Lost}-{branch.Streak}");
            // Isolated persisted health/streak branches, never edits to the natural
            // story run, production accounts, or claimed native combat outcomes.
            test.Player.Theatre5 = BsonSerializer.Deserialize<PlayerTheatre5State>(baseline);
            test.State.RequestReceipts.Clear();
            test.State.PendingMutation = null;
            test.State.PveAttempt = null;
            test.Adventure.Status = 4;
            test.Adventure.Health = maxHealth - branch.Lost;
            var chapter = test.Data.PveAdventureData!.PveChapterData!;
            chapter.ContinueWin = branch.Streak;
            test.SaveFixture();
            var chapterRow = TableReaderV2.Parse<AscNet.Table.V2.share.theatre5.theatre5pve.Theatre5PveChapterTable>()
                .Single(row => row.Id == chapter.ChapterId);
            var level = TableReaderV2.Parse<AscNet.Table.V2.share.theatre5.theatre5pve.Theatre5PveChapterLevelTable>()
                .Single(row => row.GroupId == chapterRow.LevelGroup && row.Level == chapter.CurPveChapterLevel!.Level);
            int[] authoredBuffs = level.MonsterNerfBuff.Where(id => id > 0).Distinct().ToArray();
            Require(authoredBuffs.Length > 0, "Chosen authored encounter exercises a real monster-nerf buff");
            GodfallEnterNative(test);
            AssertNerf();
            var retry = new DlcSingleEnterFightRequest { WorldId = 200, LevelId = 0 };
            test.Reload();
            test.Success(nameof(DlcSingleEnterFightRequest), retry);
            AssertNerf();
            test.Success(nameof(DlcSingleFightSettleRequest), GodfallMinimalResult(test, 3));
            test.Success(nameof(DlcSingleEnterFightRequest), retry);
            AssertNerf();

            void AssertNerf()
            {
                var attempt = GodfallAttempt(test);
                var entry = MessagePackSerializer.Deserialize<DlcSingleEnterFightResponse>(attempt.EntryResponse).WorldData!;
                var native = GodfallProjectedWorld(attempt);
                foreach (var world in new[] { entry, native })
                {
                    var buffs = world.AutoChessGameplayData!.EnemyData!.AutoChessData!.MagicIds;
                    Require(branch.Nerf == 0 ? buffs.Count == 0
                        : buffs.Count == authoredBuffs.Length && authoredBuffs.All(id => buffs.GetValueOrDefault(id) == branch.Nerf),
                        "PvE entry/native projection/retry0 use lost-health minus win-streak nerf levels, omitting nonpositive levels");
                }
            }
        }
        Require(baseline.SequenceEqual(source.State.ToBson()), "Isolated nerf fixtures do not mutate the natural story run");
    }

    private static void GodfallCheckCrossModeAttempt(GodfallCase test)
    {
        Require(test.Adventure.Status == 6, "Cross-mode preservation starts with registered native authorization");
        int mode = test.Data.PvpType;
        long pvpRun = test.State.PvpRunId, pveRun = test.State.PveRunId;
        byte[] pvpAdventure = test.Data.PvpAdventureData?.ToBson() ?? [];
        byte[] pveAdventure = test.Data.PveAdventureData?.ToBson() ?? [];
        byte[] pvpAttempt = test.State.PvpAttempt?.ToBson() ?? [];
        byte[] pveAttempt = test.State.PveAttempt?.ToBson() ?? [];
        byte[] entry = GodfallAttempt(test).EntryResponse.ToArray();
        test.Success(nameof(PveOrPvpChangeRequest), new PveOrPvpChangeRequest());
        Require(test.Data.PvpType == 3 - mode, "Registered mode switch is permitted with an active native battle");
        AssertFrozen();
        test.Reload();
        AssertFrozen();
        test.Success(nameof(PveOrPvpChangeRequest), new PveOrPvpChangeRequest());
        Require(test.Data.PvpType == mode && test.Adventure.Status == 6,
            "Returning to the original mode preserves Battling rather than implicitly settling it");
        AssertFrozen();
        GodfallEnterNative(test);
        Require(GodfallAttempt(test).EntryResponse.SequenceEqual(entry),
            "Returning across modes replays the original authorized native entry");

        void AssertFrozen()
        {
            Require(test.State.PvpRunId == pvpRun && test.State.PveRunId == pveRun
                && pvpAdventure.SequenceEqual(test.Data.PvpAdventureData?.ToBson() ?? [])
                && pveAdventure.SequenceEqual(test.Data.PveAdventureData?.ToBson() ?? [])
                && pvpAttempt.SequenceEqual(test.State.PvpAttempt?.ToBson() ?? [])
                && pveAttempt.SequenceEqual(test.State.PveAttempt?.ToBson() ?? []),
                "Mode switch/relog preserves both adventure states and both frozen native attempts");
        }
    }

    private static void GodfallCheckInterruptions()
    {
        foreach (int state in new[] { 0, 2, 3 })
        {
            using GodfallCase test = new($"minimal-settle-{state}");
            GodfallBattleFixture(test);
            if (state != 2) GodfallEnterNative(test);
            int health = test.Adventure.Health, round = test.Adventure.RoundNum;
            int discountRounds = test.Adventure.BagData.RoundNumWithoutGridUnlock;
            byte[]? enemy = state == 3 ? GodfallAttempt(test).OpponentSnapshot.ToArray() : null;
            string? room = state == 3 ? GodfallAttempt(test).RoomId : null;
            JObject result = test.Success(nameof(DlcSingleFightSettleRequest), GodfallMinimalResult(test, state));
            if (state == 3)
            {
                Require(test.Adventure.Health == health && test.Adventure.RoundNum == round && test.Adventure.Status == 5,
                    "Minimal offline interruption preserves outcome and resumes frozen matching");
                Require(test.Adventure.BagData.RoundNumWithoutGridUnlock == discountRounds,
                    "Offline interruption is not a resolved round for grid discounts");
                test.Reload();
                GodfallEnterNative(test);
                Require(GodfallAttempt(test).RoomId != room && GodfallAttempt(test).OpponentSnapshot.SequenceEqual(enemy!),
                    "Offline reentry preserves enemy but replaces attempt identity");
            }
            else if (state == 0)
            {
                Require(test.Adventure.Health == health - 1 && test.Adventure.RoundNum == round + 1,
                    "Minimal give-up state0 is an ordinary loss");
                Require(test.Adventure.BagData.RoundNumWithoutGridUnlock == discountRounds + 1,
                    "Minimal state0 give-up is a resolved loss for the unbought-grid discount");
            }
            else Require((bool)result["DlcFightSettleData"]!["XAutoChessGameplayResult"]!["IsFinish"]!,
                "Minimal advance state2 can finalize an off-battle adventure");
        }
    }

    private static void GodfallCheckOpponentSnapshots()
    {
        using GodfallCase test = new("native-human-and-robot");
        GodfallBattleFixture(test);
        // BSON snapshot of a real-shaped persisted player fixture, not a fictional bot identity.
        // FindResults does not execute Mongo predicates: hosted verification owns query isolation.
        Player human = BsonSerializer.Deserialize<Player>(test.Player.ToBson());
        human.PlayerData.Id += 100;
        human.PlayerData.Name = "Godfall persisted opponent fixture";
        var run = human.Theatre5.Data.PvpAdventureData!;
        var rank = TableReaderV2.Parse<Theatre5RankTable>().Where(row => Convert.ToInt32(row.Rating) <= run.NormalOriginRating).MaxBy(row => row.Id)!;
        var match = TableReaderV2.Parse<Theatre5MatchConfigTable>().Where(row => Convert.ToInt32(row.CupCount) == run.TrophyNum
            && Convert.ToInt32(row.DefeatCount) == 0).OrderBy(row => row.Id).ElementAt(rank.Id - 1);
        run.TrophyNum = Convert.ToInt32(match.EnemyCupCount);
        run.Health -= Convert.ToInt32(match.EnemyDefeatCount);
        test.Players.FindResults = [human];
        test.Success(nameof(Theatre5MatchRequest), new Theatre5MatchRequest());
        Require(test.State.PvpOpponentPlayerId == human.PlayerData.Id && test.State.PvpOpponentRobotId == 0,
            "Eligible persisted human is matched as a human, not an authored robot");
        byte[] snapshot = MessagePackSerializer.Serialize(test.State.PvpOpponent);
        run.CharacterLv += 3;
        human.PlayerData.Name = "Changed after matching";
        test.Reload();
        test.Success(nameof(Theatre5MatchRequest), new Theatre5MatchRequest());
        Require(MessagePackSerializer.Serialize(test.State.PvpOpponent).SequenceEqual(snapshot),
            "Repeated match/relog cannot replace frozen opponent with later live build changes");
        test.Players.FindResults = [test.Player, human];
        JObject board = test.Success(nameof(XTheatre5QueryRankRequest), new XTheatre5QueryRankRequest { CharacterId = 0 });
        var ids = board["RankPlayerInfos"]!.Select(row => (long)row["Id"]!).ToArray();
        Require(ids.Contains(test.Player.PlayerData.Id) && ids.Contains(human.PlayerData.Id) && ids.Distinct().Count() == ids.Length,
            "Overall rank exposes persisted player identities without bot entries");
        GodfallEnterNative(test);
        test.Success(nameof(DlcSingleFightSettleRequest), GodfallSyntheticNativeResult(test));
        GodfallBattleFixtureReset(test);
        test.Players.FindResults = [];
        test.Success(nameof(Theatre5MatchRequest), new Theatre5MatchRequest());
        var robot = TableReaderV2.Parse<Theatre5MatchRobotTable>().Single(row => row.Id == test.State.PvpOpponentRobotId);
        Require(test.State.PvpOpponentPlayerId == 0 && test.State.PvpOpponent!.AutoChessData!.CharacterId == robot.CharacterId
            && test.State.PvpOpponent.AutoChessData.CharacterLevel == robot.Level
            && test.State.PvpOpponent.AutoChessData.Skills.SequenceEqual(robot.SkillIds.Where(id => id > 0)),
            "Empty human pool selects a labelled authored robot with its actual build");
    }

    private static void GodfallBattleFixtureReset(GodfallCase test)
    {
        test.Adventure.Status = 4;
        test.Adventure.CharacterExp = 0;
        test.Adventure.ChooseMissions.Clear();
        test.Adventure.RandomRelics.Clear();
        test.Adventure.SkillChoiceData = null;
        test.Adventure.IsCanFreeUnlockGrid = false;
        test.SaveFixture();
    }

    private static void GodfallCheckBattleEffects()
    {
        using GodfallCase test = new("native-battle-effects");
        GodfallBattleFixture(test);
        foreach (int item in new[] { 40102, 40010 })
        {
            int instance = ++test.Adventure.IdSequence;
            test.Adventure.BagData.RelicDict[instance] = new() { InstanceId = instance, ItemId = item, ItemType = 7 };
            test.Adventure.RelicOrders.Add(instance);
        }
        test.SaveFixture();
        GodfallEnterNative(test);
        var attempt = GodfallAttempt(test);
        var native = GodfallProjectedWorld(attempt);
        Require(attempt.Effects.Any(effect => effect.Type == 1) && attempt.Effects.Any(effect => effect.Type == 9),
            "Authored battle relics emit native buff and temporary-attribute effects");
        foreach (int buff in attempt.Effects.Where(effect => effect.Type == 1).SelectMany(effect => effect.AddBuffResult!.Buffs))
            Require(native.AutoChessGameplayData!.SelfData!.AutoChessData!.MagicIds.ContainsKey(buff),
                "Frozen native projection contains notified self battle buffs");
        byte[] effects = MessagePackSerializer.Serialize(attempt.Effects);
        test.Success(nameof(DlcSingleFightSettleRequest), GodfallMinimalResult(test, 3));
        GodfallEnterNative(test);
        Require(MessagePackSerializer.Serialize(GodfallAttempt(test).Effects).SequenceEqual(effects),
            "Interrupted reentry reuses effects rather than triggering relics twice");
    }
}
