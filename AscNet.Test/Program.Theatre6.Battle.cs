using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.Table.V2.share.statussyncfight.level;
using AscNet.Table.V2.share.theatre6;
using AscNet.Table.V2.share.theatre6pvp;
using MessagePack;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using Newtonsoft.Json.Linq;
using System.Reflection;

namespace AscNet.Test;

// Native boundary, Phantom Clash and the cross-player defence hand-off. The DlcSingleEnterFight/
// DlcSingleFightSettle traffic here is SYNTHETIC: actors come from frozen entry authorization,
// zero levels resolve independently from the authored native World table, and outcomes are chosen
// by the harness. These checks prove attempt identity, authorization, exactly-once settlement and
// durable PvP state, never Theatre6 combat or rendering.
//
// Client-facing key sets follow the INSTALLED client Lua (assets/temp/lua/matrix.ab, index_sha1
// 0dc2ae92069468f963c7ce301c0eeacc5f7ed23a, file_sha1 2a84183f6f7ea02ce3ef5d57dda3f91d4b80aa4e).
// The repository PGR_DATA/en corpus is stale for XTheatre6ControlPvpNetwork.lua (installed
// 941a311b89e9cf10e8580f984c027cd6d56944ae handles a RestartFight error code explicitly and clears
// the client tiny battle state), XTheatre6Model.lua (installed cb9d70a84e3b5a58fd73502f6c7679cc7bf4f237
// also copies settleData.PassStageRecords/PassDiffRecords) and XTheatre6ControlConfig.lua (installed
// 047fb0ea22ebddc28ab9374b98bb56f1346965c0 has an optional customModelData parameter). Everything
// else is byte-identical modulo CRLF, including XTheatre6BattleAgency.lua (installed 13278 B, sha1
// 7225efeb998b802342a9b147d5adbcf8fc1e7b1d), the producer this envelope follows.
internal partial class Program
{
    private static void ValidateRequiemNativeBattleChecks()
    {
        using RequiemCase test = new("native-pve");
        test.UnlockGameplay();
        (int character, int fashion, int buff, int difficulty) = RequiemBuild(0, 0);
        string modeKey = test.StartGameplayRun(character, fashion, buff, difficulty);
        test.WalkToRoom(modeKey, 4, "native-pve");

        // The client only sends the offered side; the server binds the authored monster once.
        JObject room = test.Room(modeKey);
        int offeredFight = room.Value<int>("FightId");
        test.Call("Theatre6FightRoomSlideRequest", Req(("SelectType", 1)));
        JObject frozen = test.Room(modeKey);
        Require(frozen.Value<int>("SelectedMonsterId") > 0, "Theatre6 monster selection must freeze SelectedMonsterId.");
        Require(frozen.Value<int>("FightId") > 0, "Theatre6 monster selection must freeze the authored FightId.");
        if (offeredFight > 0)
            AssertEqual(offeredFight, frozen.Value<int>("FightId"), "Theatre6 monster selection keeps the offered fight identity");
        Require(frozen["FightRewards"] is JArray { Count: > 0 }, "Theatre6 frozen fight must publish its reward preview.");
        AssertEqual(true, TableReaderV2.Parse<Theatre6MonsterTable>()
                .Any(row => Convert.ToInt32(row.Id) == frozen.Value<int>("SelectedMonsterId")),
            "Theatre6 frozen monster must be an authored monster row");
        test.Reject("Theatre6FightRoomSlideRequest", Req(("SelectType", 2)), "Theatre6 monster reselection after the fight is frozen");

        JObject entered = test.Call("DlcSingleEnterFightRequest", test.PveNativeEntryRequest());
        JObject world = entered["WorldData"] as JObject
            ?? throw new InvalidDataException("Theatre6 native entry returned no WorldData.");
        AssertEqual(7, world.Value<int>("WorldType"), "Theatre6 native entry uses the authored DLC world type");
        JObject gameplay = world["Theatre6GameplayData"] as JObject
            ?? throw new InvalidDataException("Theatre6 native entry omitted Theatre6GameplayData.");
        Require(gameplay["SelfData"] is JObject && gameplay["EnemyData"] is JObject,
            "Theatre6 native entry must describe both native combatants.");
        // XTheatre6BattleAgency copies RoundNum only for PvP, so a PvE entry keeps the native default.
        AssertEqual(0, gameplay.Value<int?>("RoundNum") ?? -1, "Theatre6 PvE native entry keeps the client's default round number");
        Require(world["Players"] is JArray { Count: > 0 }, "Theatre6 native entry must describe the participating players.");

        // The frozen attempt is created by the entry response, so the deterministic clock can only be
        // moved once it exists: backdating before the entry finds nothing and leaves the reported
        // combat length outside the elapsed bound, which would reject every settle for the wrong
        // reason and mask the predicate each negative is actually testing.
        test.BackdateNativeAttempt();
        test.Relog("native-zero-attempt");
        JObject reentered = test.Call("DlcSingleEnterFightRequest", test.PveNativeEntryRequest());
        Require(JToken.DeepEquals(world, reentered["WorldData"]),
            "A persisted zero-level PvE attempt must retain identical authorization after relog and reentry.");

        // Theatre6-owned validator negatives. Each is rejected mutation-free and the legitimate
        // settle that follows proves the frozen attempt survived every rejection.
        test.Reject("DlcSingleFightSettleRequest", test.NativeReportMutatedActor(world, win: true),
            "Theatre6 native report with a mutated actor");
        test.Reject("DlcSingleFightSettleRequest", test.NativeReportMutatedTemplate(world, win: true),
            "Theatre6 native report with a mutated actor template");
        test.Reject("DlcSingleFightSettleRequest", test.NativeReportWithoutCheckData(world, win: true),
            "Theatre6 native report without the check block");
        test.Reject("DlcSingleFightSettleRequest", test.NativeReportInvalidRecord(world, win: true),
            "Theatre6 native report with an out-of-domain damage record");
        test.Reject("DlcSingleFightSettleRequest", test.NativeReport(world, win: true, worldId: test.RequiemPvpConfig("DlcFightWorldId")),
            "Theatre6 PvE settle for the PvP world");
        test.Reject("DlcSingleFightSettleRequest", test.NativeReport(world, win: true, levelId: 9999),
            "Theatre6 native settle for a foreign level");
        test.Reject("DlcSingleFightSettleRequest", test.NativeReport(world, win: true, levelId: -1),
            "Theatre6 native settle for a negative level");

        DlcSingleFightSettleRequest missingWeapon = test.NativeReport(world, win: true);
        Theatre5Theatre6NpcData equipped = missingWeapon.DlcReportWorldResult.DlcFightSettleData!.WorldData!
            .Theatre6GameplayData!.SelfData!;
        Require(equipped.WeaponIds is { Length: > 0 }, "The native fixture must authorize an equipped weapon.");
        int authorizedWeapon = equipped.WeaponIds[0];
        equipped.WeaponIds = null!;
        test.Reject("DlcSingleFightSettleRequest", missingWeapon,
            "Theatre6 native nil weapons cannot omit an authorized weapon");
        DlcSingleFightSettleRequest extraWeapon = test.NativeReport(world, win: true);
        Theatre5Theatre6NpcData unarmed = extraWeapon.DlcReportWorldResult.DlcFightSettleData!.WorldData!
            .Theatre6GameplayData!.EnemyData!;
        Require(unarmed.WeaponIds is null, "The native monster fixture must report its unallocated weapon list as nil.");
        unarmed.WeaponIds = [authorizedWeapon];
        test.Reject("DlcSingleFightSettleRequest", extraWeapon,
            "Theatre6 native unarmed monster cannot acquire an unauthorized weapon");
        DlcSingleFightSettleRequest changedWeapon = test.NativeReport(world, win: true);
        changedWeapon.DlcReportWorldResult.DlcFightSettleData!.WorldData!.Theatre6GameplayData!.SelfData!
            .WeaponIds[0] = checked(authorizedWeapon + 1);
        test.Reject("DlcSingleFightSettleRequest", changedWeapon,
            "Theatre6 native equipped weapon identity remains exact");
        DlcSingleFightSettleRequest duplicatedWeapon = test.NativeReport(world, win: true);
        Theatre5Theatre6NpcData duplicated = duplicatedWeapon.DlcReportWorldResult.DlcFightSettleData!.WorldData!
            .Theatre6GameplayData!.SelfData!;
        duplicated.WeaponIds = [.. duplicated.WeaponIds, authorizedWeapon];
        test.Reject("DlcSingleFightSettleRequest", duplicatedWeapon,
            "Theatre6 native equipped weapon count remains exact");
        test.Reject("DlcSingleFightSettleRequest", test.NativeReport(world, win: true, finishTime: -1),
            "Theatre6 native report with a negative native time");
        // A future authorization stamp is the only time-domain invariant left: the report's own
        // simulated duration may outrun the wall clock, but the attempt it authorizes cannot have
        // started after the settle that reports it.
        long futureStart = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 3_600_000;
        foreach (Theatre6RunState run in test.State.ActiveRuns.Values)
            if (run.NativeAttempt is { Settled: false } attempt)
                attempt.StartedAt = Math.Max(attempt.StartedAt, futureStart);
        test.SaveFixture();
        test.Reject("DlcSingleFightSettleRequest", test.NativeReport(world, win: true),
            "Theatre6 native report authorized by a future attempt start");

        // BackdateNativeAttempt keeps the EARLIER of the stored stamp and its floor, so the same call
        // restores the attempt the legitimate settles below are authorized by.
        test.BackdateNativeAttempt();
        string roomBefore = test.RoomIdentity(modeKey);
        // Native FinishTime is trunc_int32(XFight.Time) accumulated from inputdelta*TimeScale, so the
        // simulated duration can outrun the wall clock between authorization and settle: the observed
        // retail report carried 33 after 18614 ms. This is the first positive native result in the
        // suite, so a build that still bounds FinishTime by elapsed wall time fails on it here rather
        // than on a later scenario's own boundary.
        const int acceleratedFinishTime = 33;
        DlcSingleFightSettleRequest nativeReport = test.NativeReport(world, win: true, finishTime: acceleratedFinishTime);
        JObject encodedReport = JObject.Parse(MessagePackSerializer.ConvertToJson(MessagePackSerialize(nativeReport.GetType(), nativeReport)));
        AssertEqual(JTokenType.Null, encodedReport["DlcReportWorldResult"]!["DlcFightSettleData"]!["WorldData"]!
            ["Theatre6GameplayData"]!["EnemyData"]!["WeaponIds"]!.Type,
            "The native settle sends MessagePack nil, not an empty weapon array");
        AssertEqual(acceleratedFinishTime, encodedReport["DlcReportWorldResult"]!["DlcFightSettleData"]!["FinishTime"]!.Value<int>(),
            "The accelerated native report must carry the simulated time that outruns its frozen attempt");
        JObject settled = test.Call("DlcSingleFightSettleRequest", nativeReport);
        JObject settleData = settled["DlcFightSettleData"] as JObject
            ?? throw new InvalidDataException("Theatre6 native settle returned no DlcFightSettleData.");
        JObject result = settleData["ResultData"] as JObject
            ?? throw new InvalidDataException("Theatre6 native settle returned no ResultData.");
        AssertEqual(true, result.Value<bool>("IsPlayerWin"), "Theatre6 synthetic win is reported as a player win");
        JObject? fightResult = settleData["Theatre6FightResult"] as JObject;
        Require(fightResult is not null, "Theatre6 native settle must return the fight result.");
        Require(fightResult!["RewardGoodsList"] is JArray { Count: > 0 },
            "Theatre6 native win must return the authored reward preview for the reward screen.");
        test.AssertBuffRewardShape(fightResult["RewardGoodsList"] as JArray);
        Require(test.RoomIdentity(modeKey) != roomBefore || test.SettledMode(modeKey),
            "Theatre6 native win must advance the authored chain or settle the run.");

        byte[] settledState = test.State.ToBson();
        byte[] settleResponse = test.LastResponseContent;
        int settlePacket = test.LastPacketId;
        // The retransmission reuses the settled report: its body hash is the identity of the settled
        // mutation, so a report regenerated with a different native time would be a different body
        // rather than a replay.
        JObject replay = test.Call("DlcSingleFightSettleRequest", nativeReport, reusePacketId: settlePacket);
        AssertEqual(Convert.ToHexString(settleResponse), Convert.ToHexString(test.LastResponseContent),
            "Theatre6 native settle duplicate returns the frozen response bytes");
        AssertEqual(Convert.ToHexString(settledState), Convert.ToHexString(test.State.ToBson()),
            "Theatre6 native settle duplicate cannot grant the fight rewards twice");
        test.Relog("native-settle-receipt");
        string reloggedOutcome = DocumentWithoutReceipts(test.Player.ToBson());
        test.Call("DlcSingleFightSettleRequest", nativeReport);
        AssertEqual(Convert.ToHexString(settleResponse), Convert.ToHexString(test.LastResponseContent),
            "Theatre6 native settle body-hash replay after relog returns the frozen response");
        AssertEqual(reloggedOutcome, DocumentWithoutReceipts(test.Player.ToBson()),
            "Theatre6 native settle replay after relog preserves progression and rewards exactly once");
        Require(replay["DlcFightSettleData"] is JObject, "Theatre6 frozen settle response keeps its payload shape.");
        Require(test.State.Pvp.Battle is null, "A PvE native win cannot open a Phantom Clash battle.");
    }

    // Battle relic rewards. XUiPanelTheatre6RewardRelic renders the relic identity the settle announces,
    // so a fight reward that rolls the relic pool must announce the authored universal stat-only relic:
    // no character binding, no authored buffs and authored attribute terms. The character effect relics
    // (their authored buff carries no localized description, which is the row the reward card indexed
    // when it crashed) and the skill star-up relic are other authored categories. The category is
    // asserted from the authored AttrPack row the announced identity resolves to - never from pool text,
    // a weighted candidate list or a pinned pack id - and the same identity must land on the run's own
    // relic board, which is the effect the client reads back.
    private static void ValidateRequiemBattleRelicRewardsChecks()
    {
        List<Theatre6StageFightTable> relicFights = TableReaderV2.Parse<Theatre6StageFightTable>()
            .Where(fight => RequiemRelicRewardPools(fight.EasyRewardTypes, fight.EasyRewardIds).Count > 0
                || RequiemRelicRewardPools(fight.HardRewardTypes, fight.HardRewardIds).Count > 0)
            .ToList();
        Require(relicFights.Count > 0, "The authored StageFight table must publish the battle relic roll.");
        Dictionary<int, Theatre6AttrPackTable> packs = TableReaderV2.Parse<Theatre6AttrPackTable>()
            .GroupBy(row => Convert.ToInt32(row.Id)).ToDictionary(group => group.Key, group => group.First());

        int storyCharacter;
        using (RequiemCase story = new("battle-relic-story"))
        {
            story.StartStoryRun(0);
            storyCharacter = story.Mode(RequiemCase.StoryModeKey).Value<int>("CharacterId");
            Theatre6StoryLineTable line = TableReaderV2.Parse<Theatre6StoryLineTable>()
                .OrderBy(row => Convert.ToInt32(row.Id)).First();
            AssertEqual(Convert.ToInt32(line.UseCharacter is > 0 ? line.UseCharacter : line.StageCharacter), storyCharacter,
                "The story battle fixture must play the actor its common storyline authors");
            AssertRequiemBattleRelicRewards(story, RequiemCase.StoryModeKey, storyCharacter, relicFights, packs, "battle-relic-story");
        }

        (int character, int fashion, int buff, int difficulty) = RequiemBuild(0, 0);
        Require(character != storyCharacter, "The two battle relic states must cover two authored characters.");
        using (RequiemCase gameplay = new("battle-relic-gameplay"))
        {
            gameplay.UnlockGameplay();
            AssertRequiemBattleRelicRewards(gameplay, gameplay.StartGameplayRun(character, fashion, buff, difficulty),
                character, relicFights, packs, "battle-relic-gameplay");
        }
    }

    // Fixture RNG pin for the battle relic roll: the run's persisted xorshift64(13/7/17) state whose
    // SECOND next draw is 1711. The slide consumes the first draw for its own FightSeed roll, so the
    // relic roll that follows is 1711 before the pool's own modulo - one weight unit past every
    // stat-only candidate, i.e. the first candidate only a filter that still admits the character
    // effect relics can hand out. 1711 stays below every candidate total that still offers an effect
    // relic, so that draw never wraps and the pinned identity does not depend on the relics the walk
    // itself acquired (a modulus-derived seed does). Nothing else draws between this pin and the roll.
    private static readonly long RequiemBattleRelicRollState = unchecked((long)0xF79EFB69F9480EF5UL);

    // One authored battle state: the chain's own room, the frozen side's bounded relic roll, the settle
    // response the reward screen consumes and the run board that receives the announced relic.
    private static void AssertRequiemBattleRelicRewards(RequiemCase test, string modeKey, int character,
        List<Theatre6StageFightTable> relicFights, Dictionary<int, Theatre6AttrPackTable> packs, string label)
    {
        AssertEqual(character, test.Mode(modeKey).Value<int>("CharacterId"), $"{label}: the battle plays the authored character");
        HashSet<int> authoredFights = relicFights.Select(fight => Convert.ToInt32(fight.Id)).ToHashSet();

        // The authored chain leads to the room its own StageFight row gives the relic roll to. Fights the
        // chain puts in the way are cleared by the ordinary step, so the fixture never picks a room.
        JObject room = test.Room(modeKey);
        for (int step = 0; step < 512; step++)
        {
            Require(!test.ActiveRun(modeKey).Settled, $"{label}: the run settled before any authored relic battle.");
            room = test.Room(modeKey);
            if (room.Value<int>("RoomType") is 4 or 5 && room.Value<int>("SelectedMonsterId") == 0
                && authoredFights.Contains(room.Value<int>("FightId")))
                break;
            test.StepRun(modeKey, label);
        }

        int fightId = room.Value<int>("FightId");
        Require(authoredFights.Contains(fightId), $"{label}: the run never reached an authored relic battle.");
        Theatre6StageFightTable fight = relicFights.Single(row => Convert.ToInt32(row.Id) == fightId);

        test.ActiveRun(modeKey).RandomState = RequiemBattleRelicRollState;
        test.Call("Theatre6FightRoomSlideRequest", Req(("SelectType", 2)));
        room = test.Room(modeKey);
        int easyMonster = fight.EasyMonsterId;
        int hardMonster = Convert.ToInt32(fight.HardMonsterId ?? 0);
        bool hard = room.Value<int>("RoomType") == 5 && hardMonster > 0 && room.Value<int>("SelectedMonsterId") == hardMonster;
        Require(room.Value<int>("SelectedMonsterId") == (hard ? hardMonster : easyMonster),
            $"{label}: the frozen monster side must be an authored monster of the fight row.");
        List<int> relicSlots = hard
            ? RequiemRelicRewardPools(fight.HardRewardTypes, fight.HardRewardIds)
            : RequiemRelicRewardPools(fight.EasyRewardTypes, fight.EasyRewardIds);
        Require(relicSlots.Count > 0, $"{label}: the frozen battle side must roll the authored relic pool.");
        Dictionary<int, int> board = RequiemRelicBoard(test, modeKey);

        JObject entered = test.Call("DlcSingleEnterFightRequest", test.PveNativeEntryRequest());
        JObject world = entered["WorldData"] as JObject
            ?? throw new InvalidDataException($"{label}: the native entry returned no WorldData.");
        test.BackdateNativeAttempt();
        JObject settled = test.Call("DlcSingleFightSettleRequest", test.NativeReport(world, win: true));
        JArray rewards = (settled["DlcFightSettleData"] as JObject)?["Theatre6FightResult"]?["RewardGoodsList"] as JArray ?? [];

        List<int> announced = [];
        foreach (JObject reward in rewards.Children<JObject>())
        {
            int packId = reward.Value<int?>("AttrPack") ?? 0;
            AssertEqual(0, reward.Value<int?>("SkillId") ?? 0,
                $"{label}: an authored relic roll announces a relic, never a skill identity");
            Require(packs.TryGetValue(packId, out Theatre6AttrPackTable? pack),
                $"{label}: the announced relic {packId} must be an authored AttrPack row.");
            Require(!(pack!.Character is > 0),
                $"{label}: a battle relic is universal, never the character effect relic {pack.Id}.");
            Require(!(pack.BuffIds is > 0),
                $"{label}: a battle relic is stat-only, never the effect relic {pack.Id} carrying an authored buff.");
            Require(pack.AttrTypes.Count > 0 && pack.AttrNums.Count > 0,
                $"{label}: a battle relic must carry its authored attribute terms ({pack.Id}).");
            announced.Add(packId);
        }

        AssertEqual(relicSlots.Count, announced.Count, $"{label}: the frozen battle must announce exactly its authored relic rolls");
        Dictionary<int, int> granted = RequiemRelicBoard(test, modeKey);
        foreach (IGrouping<int, int> relic in announced.GroupBy(packId => packId))
            AssertEqual(board.GetValueOrDefault(relic.Key) + relic.Count(), granted.GetValueOrDefault(relic.Key),
                $"{label}: the announced battle relic must be granted to the run board exactly once ({relic.Key})");
    }

    // The relic rolls of an authored StageFight reward side: reward type 5 (skill pool) whose
    // Theatre6RandomPool row is the relic pool (Type 2). Derived from the tables, so no fight or pool id
    // is pinned in the test.
    private static List<int> RequiemRelicRewardPools(List<int> rewardTypes, List<int> rewardIds)
    {
        List<int> pools = [];
        if (rewardTypes.Count == 0)
            return pools;
        Dictionary<int, int> poolTypes = TableReaderV2.Parse<Theatre6RandomPoolTable>()
            .GroupBy(row => Convert.ToInt32(row.Id)).ToDictionary(group => group.Key, group => Convert.ToInt32(group.First().Type));
        for (int index = 0; index < rewardTypes.Count; index++)
            if (rewardTypes[index] == 5 && index < rewardIds.Count && rewardIds[index] > 0
                && poolTypes.GetValueOrDefault(rewardIds[index]) == 2)
                pools.Add(rewardIds[index]);
        return pools;
    }

    // The relic board of the run exactly as the client reads it back (ModeDataDb.AttrPacks).
    private static Dictionary<int, int> RequiemRelicBoard(RequiemCase test, string modeKey) =>
        (test.Mode(modeKey)["AttrPacks"] as JObject ?? []).Properties()
            .ToDictionary(entry => int.Parse(entry.Name), entry => entry.Value.Value<int>("Num"));

    private static void ValidateRequiemPvpChecks()
    {
        using RequiemCase test = new("pvp");
        test.UnlockGameplay();
        (int character, int fashion, int buff, int difficulty) = RequiemBuild(0, 0);
        string modeKey = test.StartGameplayRun(character, fashion, buff, difficulty);
        test.WalkRun(modeKey, "pvp-prerequisite");
        test.AcknowledgeRun(modeKey, modeId: 1, slot: 1);
        Require(test.State.Files.Count == 1, "Phantom Clash fixture requires exactly one archived run at this point.");

        // Authored gate: the PVP condition needs two saved archives, so one archive must not open it.
        (Theatre6PvpActivityTable season, int timeId, _) = RequiemGate();
        test.Reconcile(RequiemOpenMoment(timeId));
        test.Call("Theatre6GetPvpPreviewInfoRequest", null, success: false);
        test.Call("Theatre6PvpStartRequest", null, success: false);
        AssertEqual(true, test.State.Pvp.Battle is null, "Gated Theatre6 PVP start opens no battle");

        // Second archive: a durable copy of the server-produced archive at a distinct slot.
        Theatre6FileState duplicate = BsonSerializer.Deserialize<Theatre6FileState>(test.State.Files[0].ToBson());
        duplicate.SlotId = test.State.Files[0].SlotId + 1;
        test.State.Files.Add(duplicate);
        test.SaveFixture();
        test.Reconcile(RequiemOpenMoment(timeId));
        AssertEqual(2, test.State.Files.Count, "Phantom Clash fixture owns two distinct archives");

        // A real request always runs on the real clock: clearing the durable authorization must be
        // recovered by the next current-clock PvP request rather than inherited as a stale projection.
        test.State.Pvp.AuthorizedSeasonId = 0;
        test.State.Pvp.AuthorizedTimeIds = [];
        test.SaveFixture();
        JObject preview = test.Call("Theatre6GetPvpPreviewInfoRequest");
        AssertEqual(Convert.ToInt32(season.Id), test.State.Pvp.AuthorizedSeasonId,
            "A current-clock Theatre6 request recovers the stale PVP season authorization");
        int initialAp = test.RequiemPvpConfig("ActionPointInit");
        AssertEqual(initialAp, preview.Value<int>("ActionPoint"), "Phantom Clash preview reports the authored initial action points");
        AssertEqual(Convert.ToInt32(season.InitPoint), test.State.Pvp.Score, "Phantom Clash preview seeds the authored initial score");
        Require(preview["PvpRankRecords"] is JObject { Count: > 0 }, "Phantom Clash preview must publish the rank records.");

        JObject started = test.Call("Theatre6PvpStartRequest", null);
        JObject activity = started["ActivityData"] as JObject
            ?? throw new InvalidDataException("Phantom Clash start returned no ActivityData.");
        AssertEqual(Convert.ToInt32(season.InitPoint), activity.Value<int>("Score"), "Phantom Clash start reports the authored score");
        Require(activity["RankId"] is not null, "Phantom Clash start must report the authored rank.");
        JObject ap = test.Call("Theatre6PvpGetActionPointRequest");
        AssertEqual(initialAp, ap.Value<int>("ActionPoint"), "Phantom Clash action points derive from the authored config");

        // Defense: authored archive references within the authored repeat limit.
        (int firstCharacter, int firstSlot, _, _) = test.PvpArchiveRefs();
        List<object?> unowned = [Req(("CharacterId", firstCharacter), ("SlotId", 99_999)), .. test.PvpDefenceSlots().Skip(1)];
        test.Reject("Theatre6PvpUpdateDefenseRequest",
            Req(("BuffId", (object?)null), ("Slots", unowned)),
            "Phantom Clash defense with an unowned archive");
        int repeatLimit = test.RequiemPvpConfig("LineupSlotRepeatLimit");
        test.Reject("Theatre6PvpUpdateDefenseRequest",
            Req(("BuffId", (object?)null), ("Slots", Enumerable.Range(0, repeatLimit + 1)
                .Select(_ => (object?)Req(("CharacterId", firstCharacter), ("SlotId", firstSlot))).ToList())),
            "Phantom Clash defense beyond the authored repeat limit");
        int environmentBuff = test.PvpEnvironmentBuff();
        JObject defense = test.Call("Theatre6PvpUpdateDefenseRequest",
            Req(("BuffId", environmentBuff), ("Slots", test.PvpDefenceSlots())));
        AssertEqual(0, defense.Value<int>("Code"), "Phantom Clash defense accepts authored archives");
        AssertEqual(RequiemDefenceSlots(), test.State.Pvp.DefenseFiles.Count,
            "Phantom Clash defense persists the authored lineup size");
        AssertEqual(environmentBuff, test.State.Pvp.DefenseBuffId, "Phantom Clash defense persists the authored environment buff");

        // Matching: offered enemies are authored robots and the cooldown keeps the same set. The start
        // request already stamped the refresh clock, so the cooldown is aged first to reach the positive
        // path; the immediate second refresh below stays the cooldown negative.
        test.BackdatePvpRefresh();
        JObject match = test.Call("Theatre6PvpRefreshMatchRequest", null);
        JObject matchResult = match["MatchResult"] as JObject
            ?? throw new InvalidDataException("Phantom Clash refresh returned no MatchResult.");
        Require(matchResult["Enemies"] is JArray { Count: > 0 }, "Phantom Clash match must offer authored enemies.");
        JObject enemy = matchResult["Enemies"]!.Children<JObject>().First();
        Require(enemy["BattleData"] is JObject, "Phantom Clash enemy must carry its persisted battle data.");
        int enemyUid = enemy.Value<int>("Uid");
        test.Call("Theatre6PvpRefreshMatchRequest", null, success: false);
        AssertEqual(enemyUid, test.PvpOfferedEnemyUid(), "Phantom Clash cooldown keeps the offered enemy set");
        // An offer is eligible whichever kind it is: a robot must be an authored catalogue row, and a
        // saved defence must be a real player with a complete owned lineup. Requiring robots here would be
        // wrong, because the authored robot probability is a weight (rank 1 authors 80%), not a guarantee,
        // and a populated database legitimately offers humans. Deterministic robot coverage therefore
        // asserts authoredness only for the robot rows it actually receives.
        foreach (JObject offered in matchResult["Enemies"]!.Children<JObject>())
        {
            JObject battle = offered["BattleData"] as JObject
                ?? throw new InvalidDataException("Phantom Clash enemy must carry its persisted battle data.");
            int robotId = battle.Value<int>("RobotId");
            if (robotId > 0)
            {
                Require(TableReaderV2.Parse<Theatre6PvpRobotTable>().Any(robot => Convert.ToInt32(robot.Id) == robotId),
                    $"Phantom Clash offered robot {robotId}, which is not an authored robot row.");
                continue;
            }
            Require(battle.Value<long>("PlayerId") > 0 && (battle["SaveFiles"] as JArray)?.Count == RequiemDefenceSlots(),
                "A saved-defence offer must be a real player with a complete authored lineup.");
        }
        Require(test.State.Pvp.Matches.Where(row => row.RobotId > 0).All(row => TableReaderV2.Parse<Theatre6PvpRobotTable>()
            .Any(robot => Convert.ToInt32(robot.Id) == row.RobotId)), "Phantom Clash matchmaking offers authored robots.");

        // Attack: the client's filled lineup, one environment buff, one action point.
        int perBattle = test.RequiemPvpConfig("ActionPointPerCost");
        test.Reject("Theatre6PvpStartFightRequest",
            Req(("EnemyId", int.MaxValue), ("MyFileSlots", test.PvpAttackSlots()), ("BuffId", environmentBuff)),
            "Phantom Clash attack against an unoffered enemy");
        test.Reject("Theatre6PvpStartFightRequest",
            Req(("EnemyId", enemyUid), ("MyFileSlots", new List<object?> { Req(("CharacterId", firstCharacter), ("SlotId", firstSlot)) }),
                ("BuffId", environmentBuff)), "Phantom Clash attack without the authored lineup size");
        int apBefore = test.State.Pvp.ActionPoint;
        JObject fight = test.Call("Theatre6PvpStartFightRequest",
            Req(("EnemyId", enemyUid), ("MyFileSlots", test.PvpAttackSlots()), ("BuffId", environmentBuff)));
        JObject battleState = fight["BattleState"] as JObject
            ?? throw new InvalidDataException("Phantom Clash attack returned no BattleState.");
        AssertEqual(enemyUid, battleState.Value<int>("EnemyId"), "Phantom Clash battle state keeps the offered enemy identity");
        AssertEqual(apBefore - perBattle, test.State.Pvp.ActionPoint, "Phantom Clash attack consumes the authored action point cost");
        JObject nativeEntry = test.Call("DlcSingleEnterFightRequest", test.PvpNativeEntryRequest());
        JObject nativeWorld = (JObject)nativeEntry["WorldData"]!;
        int nativeLevel = Convert.ToInt32(TableReaderV2.Parse<WorldTable>()
            .Single(row => Convert.ToInt32(row.Id) == nativeWorld.Value<int>("WorldId")).DefaultLevel);
        int pveWorld = Convert.ToInt32(TableReaderV2.Parse<Theatre6ActivityTable>().First().WorldId);
        int pveLevel = Convert.ToInt32(TableReaderV2.Parse<WorldTable>()
            .Single(row => Convert.ToInt32(row.Id) == pveWorld).DefaultLevel);
        Require(nativeLevel != pveLevel, "PvE and PvP fixtures must exercise distinct native default levels.");
        test.BackdateNativeAttempt();
        test.Relog("pvp-native-zero-attempt");
        JObject zeroReentry = test.Call("DlcSingleEnterFightRequest", test.PvpNativeEntryRequest());
        Require(JToken.DeepEquals(nativeWorld, zeroReentry["WorldData"]),
            "PvP zero-level reentry after BSON relog must preserve frozen authorization.");
        JObject concreteReentry = test.Call("DlcSingleEnterFightRequest",
            Req(("WorldId", nativeWorld.Value<int>("WorldId")), ("LevelId", nativeLevel)));
        Require(JToken.DeepEquals(nativeWorld, concreteReentry["WorldData"]),
            "PvP concrete-equivalent reentry must preserve the same frozen authorization.");
        test.Reject("DlcSingleFightSettleRequest", test.NativeReport(nativeWorld, win: false, levelId: 0),
            "A native result cannot retain the entry-only zero-level sentinel");
        test.Reject("DlcSingleFightSettleRequest", test.NativeReport(nativeWorld, win: false, levelId: pveLevel),
            "Phantom Clash cannot settle using the other world's authored default level");

        // Round 1 stays restartable while the battle is unfinished, and it carries a native time below
        // five seconds: FinishTime is trunc_int32 of an accumulated float, so a short round reports a
        // small time and the domain below five is legitimate. The accelerated PvE report above proves
        // the opposite direction, and both run on the registered settle path.
        JObject roundOne = test.PvpResolveRound(win: false, finishTime: 4);
        Require(test.PvpPvpResult(roundOne)["RoundResults"] is JArray { Count: 1 },
            "Phantom Clash round history grows by exactly one settled round.");
        JObject restart = test.Call("Theatre6PvpRestartFightRequest", null);
        Require(restart["BattleState"] is JObject || restart["FightResult"] is JObject,
            "Phantom Clash restart must return either the resumed battle state or the frozen result.");
        AssertEqual(1, test.PvpRoundCount(), "Phantom Clash restart resumes exactly the settled round history");

        // Two opening losses stop the battle at round two.
        JObject secondLoss = test.PvpResolveRound(win: false);
        JObject lostResult = test.PvpPvpResult(secondLoss);
        AssertEqual(false, lostResult.Value<bool>("IsFinalWin"), "Phantom Clash two opening losses lose the battle");
        AssertEqual(2, (lostResult["RoundResults"] as JArray)?.Count ?? -1, "Phantom Clash stops after two opening losses");
        Require(test.PvpBattleRecordCount() > 0, "Phantom Clash loss must persist a battle record.");
        Require(test.State.Pvp.Battle is { Finished: true, Result: not null },
            "A finished Phantom Clash battle keeps its frozen result for restart and relog.");
        Theatre6BattleState finishedLoss = test.State.Pvp.Battle!;
        AssertEqual(2, finishedLoss.Result!.RoundResults.Count, "Frozen Phantom Clash loss keeps the settled rounds");
        AssertEqual(true, finishedLoss.Result!.NewScore <= finishedLoss.Result!.OldScore,
            "A lost Phantom Clash battle never raises the persisted score");

        // Two opening wins still play the authored third round: the match is decided by three rounds, not
        // by the first side to reach two, so round three must actually be played and must carry the verdict.
        JObject second = test.PvpStartBattle();
        test.PvpResolveRound(win: true);
        JObject twoWins = test.PvpResolveRound(win: true);
        JObject twoWinResult = test.PvpPvpResult(twoWins);
        AssertEqual(false, twoWinResult.Value<bool>("IsFinalWin"),
            "Two opening wins must not end the authored three-round match");
        AssertEqual(2, (twoWinResult["RoundResults"] as JArray)?.Count ?? -1,
            "Two opening wins keep the match alive at two settled rounds");
        JObject third = test.PvpResolveRound(win: true);
        JObject winResult = test.PvpPvpResult(third);
        AssertEqual(true, winResult.Value<bool>("IsFinalWin"), "Phantom Clash winning battle reports the final win");
        AssertEqual(3, (winResult["RoundResults"] as JArray)?.Count ?? -1, "Phantom Clash plays the third round after two opening wins");
        JObject? scoreDetail = winResult["ScoreDetail"] as JObject;
        Require(scoreDetail is not null, "Phantom Clash final result must break down the score change.");
        Require(scoreDetail!["BaseWinScore"] is not null || scoreDetail["EloScore"] is not null,
            "Phantom Clash score detail carries the authored components.");
        Require(winResult["RankId"] is not null, "Phantom Clash final result reports the resulting rank.");
        Require(second["BattleState"] is JObject, "Phantom Clash battle start opens the tiny battle state.");
        int initialRankId = TableReaderV2.Parse<Theatre6PvpRankTable>().OrderBy(row => Convert.ToInt32(row.Id))
            .First(row => Convert.ToInt32(row.MaxScore) >= Convert.ToInt32(season.InitPoint)).Id;
        if (test.State.Pvp.RankId == initialRankId)
            AssertEqual(0, (winResult["RankRewardGoods"] as JArray)?.Count ?? -1,
                "An unpromoted Phantom Clash result earns no authored rank rewards");

        // Interruption uses the dedicated give-up request, not a settle.
        test.PvpStartBattle();
        test.PvpEnterRound();
        JObject giveUp = test.Call("Theatre6PvpGiveUpFightRequest", null);
        Require(giveUp["FightResult"] is JObject, "Phantom Clash give-up must return the frozen fight result.");
        AssertEqual(false, giveUp["FightResult"]!.Value<bool>("IsFinalWin"),
            "An interrupted Phantom Clash battle is a loss even after a won round");
        Require(test.State.Pvp.Battle is { Finished: true, Result: not null },
            "An interrupted Phantom Clash battle keeps its frozen result until the next attack.");
        AssertEqual(true, test.State.Pvp.BattleRecords[0].IsAttacker, "Phantom Clash attack record is recorded as the attacker");
        AssertEqual(1, test.State.Pvp.BattleRecords[0].Status, "Phantom Clash interrupted battle is recorded as abnormal");
        Require(test.State.Pvp.BattleRecords.Count <= test.RequiemPvpConfig("MaxBattleRecordCount"),
            "Phantom Clash battle records stay within the authored cap.");

        // Ranks and records read back PERSISTED season state, and that needs an actor who is genuinely in the
        // collection the query reads: the case's own fixture keeps its document in a recording buffer the rank
        // board cannot see, which is the same root that made matchmaking look empty. This forwarding fixture
        // commits the actor's document into that store and forwards reads to it — exactly the pattern the
        // cross-player defence scenario uses — so the board is asserted against real persisted state rather
        // than having the expectation relaxed.
        RequiemPlayerCollection players = new("requiem_pvp_rank_proof");
        players.Store(test.Player);
        JObject rank = test.Call("Theatre6PvpQueryRankRequest", null);
        Require(rank["RankPlayerInfos"] is JArray, "Phantom Clash rank query must return an ordered board.");
        Require(rank.Value<int>("TotalCount") >= 1, "Phantom Clash rank board must contain the persisted player.");
        AssertEqual(true, rank.Value<int>("SelfRank") >= 1, "Phantom Clash rank query reports the persisted self position");
        JObject records = test.Call("Theatre6PvpGetBattleRecordsRequest", null);
        JObject record = (records["BattleRecords"] as JArray ?? []).Children<JObject>().FirstOrDefault()
            ?? throw new InvalidDataException("Phantom Clash battle records must contain the settled battles.");
        Require(record.Value<int>("BattleId") > 0, "Phantom Clash battle record keeps its authored identity.");
        AssertEqual(test.State.Pvp.RankId, record.Value<int>("MyRankId"), "Phantom Clash battle record keeps the resulting rank");
        Require(record["EnemyInfo"] is JObject, "Phantom Clash battle record must describe the opponent.");
        Require(TableReaderV2.Parse<Theatre6PvpRobotTable>()
            .Any(row => Convert.ToInt32(row.Id) == record["EnemyInfo"]!.Value<int>("RobotId")),
            "Phantom Clash battle record keeps the authored opponent robot.");
        AssertEqual(true, TableReaderV2.Parse<Theatre6PvpRankTable>().Any(row => Convert.ToInt32(row.Id) == test.State.Pvp.RankId),
            "Phantom Clash result keeps an authored rank id");
    }

    // Cross-player defence hand-off v3. The origin keeps an ordered outbox and the recipient keeps
    // per-attacker watermarks; delivery runs through the real players collection (the pull uses a
    // Mongo ElemMatch and Player.TryFromPlayerId), so this scenario installs a forwarding collection
    // over an isolated configured database and injects a real recipient-save failure to keep the
    // outcome queued until the defender's own later session pulls it. Every assertion is read from
    // the defender's registered responses or from the durable schema; no private helper is pinned.
    private static void ValidateRequiemDefenceHandoffChecks()
    {
        (Theatre6PvpActivityTable season, int timeId, _) = RequiemGate();
        DateTimeOffset open = RequiemOpenMoment(timeId);
        long attackerOneId = 46_950;
        long attackerTwoId = 46_951;
        long defenderId = 46_952;

        // All three sit at the authored rank whose RobotProp is zero, so every offer is a saved defence and
        // the preferred defender is guaranteed to be offered; at a lower rank the offer would depend on
        // that rank's authored robot probability instead of the fixture's intent.
        // The profile also has to make the recipient's score MOVEMENT observable. The runtime clamps a
        // score into its own rank band, so a defender seeded exactly on the band floor cannot show a
        // defence loss at all: the loss is clamped straight back to the floor and the applied change is
        // zero. Two defence losses are each bounded by the authored DefKLose - the expected-score factor
        // is strictly below one - so the shared seed sits above the floor by twice that bound, which
        // keeps every step inside the band and measurable.
        Theatre6PvpRankTable humanRank = RequiemHumanOnlyRank();
        int humanRankId = Convert.ToInt32(humanRank.Id);
        int humanScore = Convert.ToInt32(humanRank.MinScore ?? 0) + (2 * 2 * Convert.ToInt32(humanRank.DefKLose));
        using RequiemCase attackerOne = new("defence-a1", RequiemPlayerCollection.CreateFixturePlayer(attackerOneId), prepareLogin: false);
        attackerOne.SeedPvpSeason(season, humanRankId, humanScore);
        using RequiemCase attackerTwo = new("defence-a2", RequiemPlayerCollection.CreateFixturePlayer(attackerTwoId), prepareLogin: false);
        attackerTwo.SeedPvpSeason(season, humanRankId, humanScore);
        using PvpPeer defender = new("defence-d", RequiemPlayerCollection.CreateFixturePlayer(defenderId), season, humanRankId, humanScore);
        // The real forwarding collection is installed LAST, after every fixture has installed its own:
        // the innermost override then wins for the whole scenario, every session saves and every
        // matchmaking query goes through this one collection, and the shared static field is written
        // exactly once here instead of being swapped again later.
        RequiemPlayerCollection players = new("requiem_defence_proof");
        try
        {
            players.Store(attackerOne.Player);
            players.Store(attackerTwo.Player);
            players.Store(defender.Player);
            players.RequireCandidateProfile(Convert.ToInt32(season.Id), humanScore, RequiemDefenceSlots());

        attackerOne.Reconcile(open);
        attackerTwo.Reconcile(open);
        defender.Reconcile(open);
        AssertEqual(humanScore, defender.Score(),
            "The defence fixture starts from its authored human-only rank profile score");

        // Recipient save failure: the hand-off cannot land, so the outcome must stay queued on the
        // origin instead of being lost with the attacker's response.
        players.ArmSaveFailure(defenderId);
        JObject settled;
        try
        {
            // The defence outcome is queued when the battle finishes, not after a single round, so the
            // whole battle has to be played inside the armed window: with three wins it needs all three
            // rounds before the hand-off is attempted.
            settled = attackerOne.PvpSettleAgainst(defenderId, win: true);
            attackerOne.PvpResolveRound(win: true);
            attackerOne.PvpResolveRound(win: true);
        }
        finally
        {
            players.ClearSaveFailure();
        }

        Require(settled["DlcFightSettleData"] is JObject, "The attacker must settle a Phantom Clash battle.");
        Theatre6DefenseOutcome outcome = attackerOne.State.Pvp.PendingDefenseOutcomes
            .SingleOrDefault(entry => entry.DefenderId == defenderId)
            ?? throw new InvalidDataException("A committed attacker settlement must queue exactly one defence outcome.");
        AssertEqual(1, outcome.BattleId, "The first attacker's defence outcome carries its own battle identity");
        AssertEqual(Convert.ToInt32(season.Id), outcome.SeasonId, "The queued outcome is scoped to the authored season");
        AssertEqual(attackerOneId, outcome.AttackerId, "The queued outcome identifies its attacker");
        Require(attackerOne.State.Pvp.AppliedDefenseWatermarks.Count == 0,
            "Only the recipient records defence watermarks.");
        AssertEqual(humanScore, defender.Score(),
            "A failed hand-off must leave the recipient score untouched until it pulls");
        byte[] attackerBeforeReplay = attackerOne.State.ToBson();

        // The defender's own later session pulls the queued outcome: exactly one record and one move.
        int defenderScoreBefore = defender.Score();
        defender.Reconcile(open);
        JObject activity = defender.Call("Theatre6PvpStartRequest", null)["ActivityData"] as JObject
            ?? throw new InvalidDataException("The defender must read its own activity data.");
        int defenderScoreAfter = activity.Value<int>("Score");
        AssertEqual(true, defenderScoreAfter != defenderScoreBefore,
            "The pulled defence outcome must move the defender's current score");
        List<JObject> defenceRecords = (defender.Call("Theatre6PvpGetBattleRecordsRequest", null)["BattleRecords"] as JArray ?? [])
            .Children<JObject>().Where(record => !record.Value<bool>("IsAttacker")).ToList();
        Require(defenceRecords.Count == 1, "The defender must read exactly one authored defence record.");
        AssertEqual(defenderScoreAfter - defenderScoreBefore, defenceRecords[0].Value<int>("ScoreChange"),
            "The defence record score change must match the applied score movement");
        // The recipient retires the entry in the origin's COMMITTED document, which is the contract that
        // matters: a fixture player's in-memory copy is its own object and is not what the runtime reads,
        // and an origin that later commits that stale copy can only resurrect the entry, never re-apply it,
        // because the recipient's watermark already accounts for the battle.
        AssertEqual(0, players.DurablePvp(attackerOneId).PendingDefenseOutcomes.Count(entry => entry.DefenderId == defenderId),
            "A durable pull retires the origin's committed outbox entry");
        AssertEqual(outcome.BattleId, defender.Watermark(season.Id, attackerOneId),
            "The recipient records the per-attacker battle watermark");
        byte[] defenderAfterPull = defender.Document();

        // Replaying the identical attacker settlement must not credit the defence a second time.
        attackerOne.Call("DlcSingleFightSettleRequest", attackerOne.LastSettleReport!, reusePacketId: attackerOne.LastPacketId);
        AssertEqual(Convert.ToHexString(attackerBeforeReplay), Convert.ToHexString(attackerOne.State.ToBson()),
            "A replayed attacker settlement cannot duplicate the outbox entry or move the attacker score");
        defender.Reconcile(open);
        AssertEqual(defenderScoreAfter, defender.Score(), "A replayed attacker settlement cannot move the defender score again");
        AssertEqual(1, (defender.Call("Theatre6PvpGetBattleRecordsRequest", null)["BattleRecords"] as JArray ?? [])
            .Children<JObject>().Count(record => !record.Value<bool>("IsAttacker")),
            "A replayed attacker settlement cannot append a second defence record");
        AssertEqual(DocumentWithoutReceipts(defenderAfterPull), DocumentWithoutReceipts(defender.Document()),
            "A replayed attacker settlement changes nothing on the defender but the request-receipt ledger");
        AssertEqual(outcome.BattleId, defender.Watermark(season.Id, attackerOneId),
            "A replayed attacker settlement cannot move the defender's battle watermark");

        // A rejected report is not a rejected commit: tampering must not reach the recipient either.
        byte[] defenderBeforeTamper = defender.Document();
        byte[] attackerBeforeTamper = attackerOne.State.ToBson();
        attackerOne.Reject("DlcSingleFightSettleRequest", attackerOne.TamperedSettleReport(),
            "tampered attacker settlement against a live defence outbox");
        AssertEqual(Convert.ToHexString(defenderBeforeTamper), Convert.ToHexString(defender.Document()),
            "A rejected attacker report leaves the defender document unchanged");
        AssertEqual(Convert.ToHexString(attackerBeforeTamper), Convert.ToHexString(attackerOne.State.ToBson()),
            "A rejected attacker report leaves the attacker document unchanged");

        // A failed attacker commit must lose the TERMINAL settlement that CREATES the defence payload, not an
        // early round: the outbox entry only materialises with the round that decides the match, so failing a
        // partial round would prove nothing about defensive durability. The match is therefore taken to two
        // opening wins first, the third round is entered while still unarmed, and only then is the durable-save
        // failure armed and that frozen third world settled DIRECTLY — the helper's re-entry would itself
        // commit and could fail instead, so the terminal settlement is made the only step that can fail.
        attackerTwo.PvpBeginAgainst(defenderId);
        attackerTwo.PvpResolveRound(win: true);
        attackerTwo.PvpResolveRound(win: true);
        attackerTwo.PvpEnterRound();
        attackerTwo.BackdateNativeAttempt();

        // Snapshots belong HERE, immediately after the third entry and before the failure is armed: the two
        // committed wins and the entry have already moved both documents legitimately, so a pre-battle
        // snapshot would assert against a state the attacker is no longer in.
        byte[] attackerBeforeCommitFailure = attackerTwo.State.ToBson();
        byte[] defenderBeforeCommitFailure = defender.Document();
        // The watermark lookup reports absence as -1 rather than a stored zero, so the invariant has to be
        // "unchanged" against whatever it is before the armed settlement — including "still absent".
        int watermarkBeforeCommitFailure = defender.Watermark(season.Id, attackerTwoId);
        DlcSingleFightSettleRequest terminal = attackerTwo.FrozenSettleReport(win: true);
        players.ArmSaveFailure(attackerTwoId);
        try
        {
            attackerTwo.Reject("DlcSingleFightSettleRequest", terminal,
                "an attacker terminal settlement whose durable save failed");
        }
        finally
        {
            players.ClearSaveFailure();
        }

        AssertEqual(Convert.ToHexString(attackerBeforeCommitFailure), Convert.ToHexString(attackerTwo.State.ToBson()),
            "A failed attacker commit must leave the attacker document exactly as the third entry left it");
        AssertEqual(Convert.ToHexString(defenderBeforeCommitFailure), Convert.ToHexString(defender.Document()),
            "A failed attacker commit must leave the recipient untouched");
        AssertEqual(0, players.DurablePvp(attackerTwoId).PendingDefenseOutcomes.Count(entry => entry.DefenderId == defenderId),
            "A failed attacker commit must not commit a defence payload");
        AssertEqual(watermarkBeforeCommitFailure, defender.Watermark(season.Id, attackerTwoId),
            "A failed attacker commit must not reach the recipient's watermark");

        // The terminal settlement is still pending, so replaying that same frozen report must now apply it —
        // and, with the first attacker's battle id, prove the two attackers stay independent.
        JObject recoveredTerminal = attackerTwo.Call("DlcSingleFightSettleRequest", terminal);
        Require(recoveredTerminal["DlcFightSettleData"] is JObject,
            "The recovered terminal settlement must settle against the same defender.");
        // The replay of the pending mutation can hand the outcome over inline on that same commit, so the
        // origin's outbox may legitimately be empty here: what has to hold either way is the identity of the
        // outcome. A still-queued entry must carry the same attacker-local battle id as the first attacker's,
        // and when it was already delivered the recipient's watermark carries that same id below, so the
        // collision is proven from whichever side is authoritative rather than by demanding a pending queue.
        var queuedOutcomes = players.DurablePvp(attackerTwoId).PendingDefenseOutcomes
            .Where(entry => entry.DefenderId == defenderId).ToList();
        Require(queuedOutcomes.Count <= 1, "The recovered terminal queues at most one outcome for the defender.");
        AssertEqual(1, queuedOutcomes.Count > 0 ? queuedOutcomes[0].BattleId : players.DurableWatermark(defenderId, season.Id, attackerTwoId),
            "Both attackers allocate the same attacker-local battle id");
        AssertEqual(0, attackerTwo.State.Pvp.AppliedDefenseWatermarks.Count,
            "Only the recipient records watermarks, never the origin");

        // The delivery resolved the defender through the committed document, because no fixture session is
        // registered for it, so the peer is rebuilt from that document in the same shape Relog uses before it
        // acts: reading its own mirror would be stale, and its next save would write the mirror back over the
        // delivered watermark, score and records.
        int recipientScoreBeforeDelivery = defender.Score();
        defender.ReloadFromCommitted();
        int deliveredScore = players.DurablePvp(defenderId).Score;
        AssertEqual(true, deliveredScore != recipientScoreBeforeDelivery,
            "A colliding attacker-local battle id from another attacker still applies to the defender");
        AssertEqual(1, players.DurableWatermark(defenderId, season.Id, attackerTwoId),
            "The second attacker's watermark is tracked separately from the first");
        AssertEqual(outcome.BattleId, players.DurableWatermark(defenderId, season.Id, attackerOneId),
            "The first attacker's watermark is unchanged by the second attacker");
        Require(players.DurablePvp(defenderId).BattleRecords.Count(record => !record.IsAttacker) == 2,
            "Both attackers appear in the defender's committed records.");

        // The recipient's own request must neither undo the delivery nor apply anything twice.
        defender.Reconcile(open);
        AssertEqual(deliveredScore, players.DurablePvp(defenderId).Score,
            "The recipient's own request must not move its delivered score");
        AssertEqual(1, players.DurableWatermark(defenderId, season.Id, attackerTwoId),
            "The recipient's own request must not disturb the delivered watermark");
        Require(players.DurablePvp(defenderId).BattleRecords.Count(record => !record.IsAttacker) == 2,
            "The recipient's own request must not duplicate the delivered defence records.");

        // The defender's own write after delivery must not erase the applied records or watermarks.
        defender.Call("Theatre6PvpUpdateDefenseRequest", Req(("BuffId", defender.DefenseBuff()), ("Slots", defender.ArchiveSlots())));
        AssertEqual(2, (defender.Call("Theatre6PvpGetBattleRecordsRequest", null)["BattleRecords"] as JArray ?? [])
            .Children<JObject>().Count(record => !record.Value<bool>("IsAttacker")),
            "An online defender save keeps every delivered defence record");
        AssertEqual(outcome.BattleId, defender.Watermark(season.Id, attackerOneId),
            "An online defender save keeps the delivered watermark");
        AssertEqual(deliveredScore, players.DurablePvp(defenderId).Score,
            "An online defender save cannot re-apply a delivered outcome");
        }
        finally
        {
            players.Dispose();
        }
    }

    private sealed partial class RequiemCase
    {
        private JObject? pvpEntryWorld;

        public DlcSingleFightSettleRequest? LastSettleReport { get; private set; }

        public Dictionary<string, object?> PveNativeEntryRequest() =>
            Req(("WorldId", Convert.ToInt32(TableReaderV2.Parse<Theatre6ActivityTable>().First().WorldId)), ("LevelId", 0));

        // The live run of a mode key, addressed by the same mode ids the chain uses.
        public Theatre6RunState ActiveRun(string modeKey) =>
            State.ActiveRuns.TryGetValue(modeKey == PlayModeKey ? 1 : 2, out Theatre6RunState? run)
                ? run
                : throw new InvalidDataException($"{Name}: no active run in {modeKey}.");

        // Subsequent PvP entry requests use the concrete level returned by the native result.
        public Dictionary<string, object?> PvpNativeEntryRequest() => Req(
            ("WorldId", pvpEntryWorld?.Value<int>("WorldId") ?? RequiemPvpConfig("DlcFightWorldId")),
            ("LevelId", pvpEntryWorld is not null && LastSettleReport is { } report ? ReportWorld(report).LevelId : 0));

        public int RequiemPvpConfig(string key) => Convert.ToInt32(TableReaderV2.Parse<Theatre6PvpConfigTable>()
            .Single(row => row.Key == key).Values);

        public int PvpOfferedEnemyUid() => State.Pvp.Matches.FirstOrDefault()?.Uid ?? 0;

        public int PvpRoundCount() => State.Pvp.Battle?.RoundResults.Count ?? 0;

        public int PvpBattleRecordCount() => State.Pvp.BattleRecords.Count;

        public JObject PvpPvpResult(JObject settleResult) =>
            settleResult["DlcFightSettleData"]?["Theatre6PvpFightResult"] as JObject
            ?? settleResult["FightResult"] as JObject
            ?? throw new InvalidDataException($"{Name}: Phantom Clash round did not return the frozen PvP result.");

        // The client fills every attack position; unique archives stay within the authored limit.
        // The attack lineup fills every attack position the client requires, drawing from at most the
        // authored SlotAttackLineupLimit distinct archives and repeating the first exactly as the client's
        // own one-click lineup does. Filling every position with a distinct archive would exceed that
        // limit, which the server rejects as an attack-count mismatch.
        public List<object?> PvpAttackSlots()
        {
            List<Theatre6FileState> files = State.Files.OrderBy(file => file.CharacterId).ThenBy(file => file.SlotId).ToList();
            int distinct = RequiemPvpConfig("SlotAttackLineupLimit");
            if (files.Count < distinct)
                throw new InvalidDataException($"{Name}: Phantom Clash requires {distinct} distinct authored archives.");
            List<object?> slots = [];
            for (int index = 0; index < RequiemAttackPositions(); index++)
            {
                Theatre6FileState file = files[index % distinct];
                slots.Add(Req(("CharacterId", file.CharacterId), ("SlotId", file.SlotId)));
            }

            return slots;
        }

        public (int FirstCharacter, int FirstSlot, int SecondCharacter, int SecondSlot) PvpArchiveRefs()
        {
            List<Theatre6FileState> files = State.Files.OrderBy(file => file.CharacterId).ThenBy(file => file.SlotId).ToList();
            if (files.Count < 2)
                throw new InvalidDataException($"{Name}: Phantom Clash requires two authored archives.");
            return (files[0].CharacterId, files[0].SlotId, files[1].CharacterId, files[1].SlotId);
        }

        // The authored environment buff for the current rank; rank one legitimately has no group.
        public int PvpEnvironmentBuff()
        {
            Theatre6PvpRankTable? rank = TableReaderV2.Parse<Theatre6PvpRankTable>()
                .FirstOrDefault(row => Convert.ToInt32(row.Id) == State.Pvp.RankId && Convert.ToInt32(row.PvpBuffGroupId ?? 0) > 0);
            if (rank is null) return 0;
            Theatre6PvpBuffGroupTable group = TableReaderV2.Parse<Theatre6PvpBuffGroupTable>()
                .Single(row => Convert.ToInt32(row.Id) == rank.PvpBuffGroupId);
            return group.AttBuffs.Count > 0 ? group.AttBuffs[0] : 0;
        }

        // Opens an authored battle. The persisted matchmaking cooldown is reset to its pre-refresh
        // state so every battle re-rolls legitimately, and a required defender must be offered.
        public JObject PvpStartBattle(long? preferredEnemyPlayerId = null)
        {
            State.Pvp.LastRefreshTime = 0;
            State.Pvp.RefreshPeriodStart = 0;
            State.Pvp.RefreshPeriodCount = 0;
            SaveFixture();
            JObject match = Call("Theatre6PvpRefreshMatchRequest", null);
            List<JObject> offered = (match["MatchResult"]?["Enemies"] as JArray ?? []).Children<JObject>().ToList();
            JObject? enemy = preferredEnemyPlayerId is long preferred
                ? offered.FirstOrDefault(candidate => candidate["BattleData"]?.Value<long>("PlayerId") == preferred)
                : offered.FirstOrDefault();
            if (preferredEnemyPlayerId is long required && enemy is null)
                throw new InvalidDataException($"{Name}: Phantom Clash matchmaking did not offer the authored defender {required}.");
            if (enemy is null)
                throw new InvalidDataException($"{Name}: Phantom Clash matchmaking offered no authored enemy.");
            JObject fight = Call("Theatre6PvpStartFightRequest", Req(
                ("EnemyId", enemy.Value<int>("Uid")), ("MyFileSlots", PvpAttackSlots()), ("BuffId", PvpEnvironmentBuff())));
            Require(fight["BattleState"] is JObject, "Phantom Clash attack must open a tiny battle state.");
            AssertEqual(enemy.Value<int>("Uid"), fight["BattleState"]!.Value<int>("EnemyId"),
                "Phantom Clash battle state keeps the offered enemy identity");
            pvpEntryWorld = null;
            return fight;
        }

        public void PvpBeginAgainst(long defenderId) => PvpStartBattle(defenderId);

        // A defence lineup fills the authored MaxSlotDefenseLineupLimit - the same count the client's
        // confirm button requires - drawing from the archives the player owns and repeating the first
        // exactly as the client's own one-click lineup does. It does not require one distinct archive per
        // position: the authored LineupSlotRepeatLimit allows a repeat, which is how a lineup is filled
        // from the two archives a single run naturally earns.
        public List<object?> PvpDefenceSlots()
        {
            int positions = RequiemDefenceSlots();
            List<Theatre6FileState> files = State.Files.OrderBy(file => file.CharacterId).ThenBy(file => file.SlotId).ToList();
            if (files.Count == 0)
                throw new InvalidDataException($"{Name}: Phantom Clash defence requires at least one owned archive.");
            List<object?> lineup = new();
            for (int index = 0; index < positions; index++)
            {
                Theatre6FileState file = files[index % files.Count];
                lineup.Add(Req(("CharacterId", file.CharacterId), ("SlotId", file.SlotId)));
            }
            return lineup;
        }

        // One native round: entry, synthetic settle, echoed world kept for the next round.
        public JObject PvpSettleStarted(bool win, bool? success = true, int finishTime = 5)
        {
            JObject entered = Call("DlcSingleEnterFightRequest", PvpNativeEntryRequest());
            JObject world = entered["WorldData"] as JObject
                ?? throw new InvalidDataException($"{Name}: Phantom Clash native entry returned no WorldData.");
            AssertEqual(7, world.Value<int>("WorldType"), "Phantom Clash native entry uses the authored DLC world type");
            JObject pvpGameplay = world["Theatre6GameplayData"] as JObject
                ?? throw new InvalidDataException($"{Name}: Phantom Clash native entry omitted the gameplay data.");
            AssertEqual(PvpRoundCount() + 1, pvpGameplay.Value<int?>("RoundNum") ?? -1,
                "Phantom Clash native entry reports the 1-based authored round index");
            if (pvpGameplay["RoundResults"] is JArray settledRounds)
                AssertEqual(PvpRoundCount(), settledRounds.Count,
                    "Phantom Clash native entry publishes the settled round history for the resumed client");
            pvpEntryWorld = world;
            LastSettleReport = NativeReport(world, win, finishTime: finishTime);
            BackdateNativeAttempt();
            JObject settled = Call("DlcSingleFightSettleRequest", LastSettleReport, success);
            Require(settled["DlcFightSettleData"] is JObject, "Phantom Clash round settlement must return DlcFightSettleData.");
            return settled;
        }

        public JObject PvpSettleAgainst(long defenderId, bool win)
        {
            PvpBeginAgainst(defenderId);
            return PvpSettleStarted(win);
        }

        public JObject PvpResolveRound(bool win, int finishTime = 5) => PvpSettleStarted(win, finishTime: finishTime);

        // Entry only: the interruption fixture opens native combat and then gives the battle up.
        public void PvpEnterRound()
        {
            JObject entry = Call("DlcSingleEnterFightRequest", PvpNativeEntryRequest());
            pvpEntryWorld = entry["WorldData"] as JObject
                ?? throw new InvalidDataException($"{Name}: Phantom Clash native entry returned no WorldData.");
        }

        // The typed settlement body for the currently frozen entry. A caller that must fail exactly one step
        // sends this directly instead of going through the round helper, which re-enters before every settle.
        public DlcSingleFightSettleRequest FrozenSettleReport(bool win)
        {
            JObject world = pvpEntryWorld
                ?? throw new InvalidDataException($"{Name}: no frozen Phantom Clash entry world to settle.");
            return NativeReport(world, win);
        }

        // A tampered attacker report for the rejected-report boundary, built from the frozen entry.
        public DlcSingleFightSettleRequest TamperedSettleReport()
        {
            JObject world = pvpEntryWorld
                ?? throw new InvalidDataException($"{Name}: no frozen Phantom Clash entry world to tamper with.");
            return NativeReportMutatedActor(world, win: true);
        }

        // Authored archives, season authorization and defence lineup for transfer fixtures; the
        // scenario still drives reconcile and every request through the registered path.
        // `rankId`/`score` place the fixture at a specific authored rank profile; the default is the rank
        // the authored initial score lands in.
        public void SeedPvpSeason(Theatre6PvpActivityTable season, int? rankId = null, int? score = null)
        {
            // A saved defence may only reference archives the player owns: that is the only kind of
            // lineup the client can build, so the owned archives and the lineup are seeded with the same
            // authored identities instead of a lineup that outnumbers what the fixture owns.
            int defenceSlots = RequiemDefenceSlots();
            State.Files.AddRange(RequiemPvpArchives(defenceSlots));
            Theatre6PvpRankTable rank = rankId is int wanted
                ? TableReaderV2.Parse<Theatre6PvpRankTable>().Single(row => Convert.ToInt32(row.Id) == wanted)
                : TableReaderV2.Parse<Theatre6PvpRankTable>().OrderBy(row => Convert.ToInt32(row.Id))
                    .First(row => Convert.ToInt32(row.MaxScore) >= Convert.ToInt32(season.InitPoint));
            State.Pvp.AuthorizedSeasonId = Convert.ToInt32(season.Id);
            State.Pvp.AuthorizedTimeIds = [Convert.ToInt32(season.TimeId)];
            State.Pvp.InitializedSeasonId = Convert.ToInt32(season.Id);
            State.Pvp.Score = score ?? Convert.ToInt32(season.InitPoint);
            State.Pvp.RankId = Convert.ToInt32(rank.Id);
            State.Pvp.CurrentRankMinScore = Convert.ToInt32(rank.MinScore ?? 0);
            // Theatre6PvpConfig.UnlockPvpModeConditionId is condition 1021100, whose authored formula is
            // 1021001&1021104: stage 2 cleared at least once, and at least two owned archives (the owned
            // archives above). Both are seeded so the gate is satisfied by authored prerequisites
            // rather than widened for the fixture.
            State.PassStageRecords[2] = 1;
            State.Pvp.ActionPoint = RequiemPvpConfig("ActionPointInit");
            // The owned archives above are what the attack lineup and an explicit defence update draw on.
            // No defence lineup is published here on purpose: matchmaking only offers saved defences, so a
            // case that leaves its lineup empty is never a candidate, and a human-only rank profile then
            // has exactly the one candidate a defence scenario intends.
            State.Pvp.DefenseFiles = [];
            State.Pvp.DefenseBuffId = 0;
            SaveFixture();
        }

        public void SolveFight(string modeKey, string fightKey, string label, bool win)
        {
            JObject entered = Call("DlcSingleEnterFightRequest", PveNativeEntryRequest());
            JObject world = entered["WorldData"] as JObject
                ?? throw new InvalidDataException($"{label}: Theatre6 native entry returned no WorldData for {fightKey}.");
            Require(world["Theatre6GameplayData"] is JObject,
                $"{label}: Theatre6 native entry omitted the gameplay data for {fightKey}.");
            BackdateNativeAttempt();
            JObject settled = Call("DlcSingleFightSettleRequest", NativeReport(world, win));
            JObject result = settled["DlcFightSettleData"]?["ResultData"] as JObject
                ?? throw new InvalidDataException($"{label}: Theatre6 native settle returned no ResultData for {fightKey}.");
            AssertEqual(win, result.Value<bool>("IsPlayerWin"), $"{label}: Theatre6 native outcome {fightKey} is reported faithfully");
        }

        // The settlement validates the reported combat length against the frozen attempt's own
        // clock, so the harness moves that persisted start back instead of sleeping. The value is
        // written in the same unit the field already holds, so either unit stays monotonic. Call it
        // after the entry created the attempt and before the settlement that validates against it.
        public void BackdateNativeAttempt(int seconds = 10)
        {
            long floor = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - (seconds * 1000L);
            foreach (Theatre6RunState run in State.ActiveRuns.Values)
                if (run.NativeAttempt is { Settled: false } attempt)
                    attempt.StartedAt = Math.Min(attempt.StartedAt, floor);
            if (State.Pvp.Battle is { Finished: false } battle)
                battle.StartedAt = Math.Min(battle.StartedAt, floor);
            SaveFixture();
        }

        // Entering Phantom Clash already publishes an offer and stamps the refresh clock, which is what the
        // retail start response shows (a last-refresh time with a full cooldown still to run), so a manual
        // refresh immediately afterwards is correctly refused. This ages that persisted stamp instead of
        // sleeping, so the positive refresh is testable without weakening the cooldown.
        public void BackdatePvpRefresh(int seconds = 6)
        {
            long floor = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - seconds;
            if (State.Pvp.LastRefreshTime > floor) State.Pvp.LastRefreshTime = floor;
            SaveFixture();
        }

        // The authored expiry is a persisted timestamp, so a fixture can age the battle instead of waiting.
        public void PvpBattleExpire()
        {
            if (State.Pvp.Battle is { } battle)
            {
                battle.ExpireAt = 0;
                SaveFixture();
            }
        }

        // Synthetic actors and round history come from frozen entry authorization; outcome is chosen.
        // Native level selection is independently modeled from the authoritative World table.
        // It is built from the installed DTOs rather than a loosened dictionary shape: the entry world is
        // reconstructed through the real MessagePack contract, so anything that cannot round-trip fails
        // here, in the fixture, instead of surfacing as an opaque deserialization failure on the server.
        public DlcSingleFightSettleRequest NativeReport(JObject world, bool win, int? worldId = null,
            int? levelId = null, int finishTime = 5)
        {
            int owner = checked((int)Player.PlayerData.Id);
            Theatre5WorldData echoed = RequiemNativeWorld(world);
            // XTheatre6BattleAgency._GetXWorldGameplayData assigns WeaponIds only when nonempty.
            // Native XTheatre6NpcData's constructor leaves this array null (unlike its allocated lists/maps).
            foreach (Theatre5Theatre6NpcData actor in new[] { echoed.Theatre6GameplayData!.SelfData!, echoed.Theatre6GameplayData.EnemyData! })
                if (actor.WeaponIds is { Length: 0 }) actor.WeaponIds = null!;
            if (echoed.LevelId == 0)
                echoed.LevelId = Convert.ToInt32(TableReaderV2.Parse<WorldTable>()
                    .Single(row => Convert.ToInt32(row.Id) == echoed.WorldId).DefaultLevel);
            if (worldId is int overriddenWorld) echoed.WorldId = overriddenWorld;
            if (levelId is int overriddenLevel) echoed.LevelId = overriddenLevel;
            return new DlcSingleFightSettleRequest
            {
                DlcReportWorldResult = new Theatre5DlcReportWorldResult
                {
                    DlcFightSettleData = new Theatre5DlcFightResultData
                    {
                        IsPlayerWin = win,
                        FinishTime = finishTime,
                        SettleState = 0,
                        WorldData = echoed,
                        PlayerData = new Dictionary<int, Theatre5DlcFightResultPlayerData>
                        {
                            // Native settlement writes the same truncated duration it reports as
                            // FinishTime into each player's SettleTime, so both carry one value.
                            [owner] = new Theatre5DlcFightResultPlayerData
                            {
                                PlayerId = owner, IsWin = win, IsSettled = true, SettleTime = finishTime
                            }
                        },
                        Theatre6CheckData = new Theatre6CheckData
                        {
                            MyData = RequiemCheckItem(null),
                            EnemyData = RequiemCheckItem(null)
                        }
                    }
                }
            };
        }

        public DlcSingleFightSettleRequest NativeReportMutatedActor(JObject world, bool win)
        {
            DlcSingleFightSettleRequest report = NativeReport(world, win);
            Theatre5Theatre6NpcData self = ReportWorld(report).Theatre6GameplayData!.SelfData!;
            int first = self.Attribs.Keys.OrderBy(key => key).FirstOrDefault();
            if (first == 0) self.Attribs[1] = 1;
            else self.Attribs[first] = self.Attribs[first] + 1;
            return report;
        }

        public DlcSingleFightSettleRequest NativeReportMutatedTemplate(JObject world, bool win)
        {
            DlcSingleFightSettleRequest report = NativeReport(world, win);
            Theatre5Theatre6NpcData self = ReportWorld(report).Theatre6GameplayData!.SelfData!;
            self.TemplateId += 1;
            return report;
        }

        public DlcSingleFightSettleRequest NativeReportWithoutCheckData(JObject world, bool win)
        {
            DlcSingleFightSettleRequest report = NativeReport(world, win);
            ReportSettle(report).Theatre6CheckData = null;
            return report;
        }

        // Records outside the authored category domain (only 0 and 1 are admissible).
        public DlcSingleFightSettleRequest NativeReportInvalidRecord(JObject world, bool win)
        {
            DlcSingleFightSettleRequest report = NativeReport(world, win);
            ReportSettle(report).Theatre6CheckData!.MyData = RequiemCheckItem(2);
            return report;
        }

        // The JSON snapshot is a view of the typed entry response; converting it back through the
        // installed contract keeps the fixture's payload schema-exact.
        private static Theatre5WorldData RequiemNativeWorld(JObject world) =>
            MessagePackSerializer.Deserialize<Theatre5WorldData>(MessagePackSerializer.Serialize(RequiemPlain(world)!));

        private static Theatre5DlcFightResultData ReportSettle(DlcSingleFightSettleRequest report) =>
            report.DlcReportWorldResult.DlcFightSettleData!;

        private static Theatre5WorldData ReportWorld(DlcSingleFightSettleRequest report) =>
            ReportSettle(report).WorldData!;
    }

    // One admissible check block; an extra category marks the record domain violation.
    private static Theatre6CheckNpcData RequiemCheckItem(int? extraCategory)
    {
        Dictionary<int, Dictionary<int, int>> damage = new()
        {
            [0] = new Dictionary<int, int> { [0] = 0 },
            [1] = new Dictionary<int, int> { [0] = 0 }
        };
        if (extraCategory is int extra) damage[extra] = new Dictionary<int, int> { [0] = 1 };
        return new Theatre6CheckNpcData
        {
            TotalDamage = 0,
            TotalEnergyCast = 0,
            DamageRecord = damage,
            EnergyCastRecord = new Dictionary<int, Dictionary<int, int>> { [0] = new(), [1] = new() },
            SkillCountRecord = new Dictionary<int, int>()
        };
    }
    // A real players collection in the configured database, wrapped in a forwarding proxy so a
    // recipient or origin save can be failed deterministically. The collection name is unique per
    // run, so the dispose-time drop can only ever remove data this scenario created, and the
    // process-wide field is restored afterwards, so no other case observes the swap.
    private sealed class RequiemPlayerCollection : IDisposable
    {
        private readonly FieldInfo field;
        private readonly object? original;
        private readonly RequiemSaveFaultProxy proxy;
        public IMongoCollection<Player> Players { get; }

        public RequiemPlayerCollection(string prefix)
        {
            field = MongoCollectionOverride.RequiredCollectionField(typeof(Player));
            original = field.GetValue(null);
            IMongoCollection<Player> real;
            try
            {
                // Unique per run: the scenario drops exactly what it created and never touches
                // pre-existing database content.
                real = AscNet.Common.Common.db.GetCollection<Player>($"{prefix}_{Guid.NewGuid():N}");
                real.Database.RunCommand<BsonDocument>(new BsonDocument("ping", 1));
            }
            catch (MongoException exception)
            {
                throw new InvalidDataException(
                    "The multi-player Theatre6 defence proof requires the isolated configured MongoDB fixture; " +
                    "the configured server did not answer.", exception);
            }

            Players = DispatchProxy.Create<IMongoCollection<Player>, RequiemSaveFaultProxy>();
            proxy = (RequiemSaveFaultProxy)(object)Players;
            proxy.Inner = real;
            MongoCollectionOverride.SetStaticField(field, Players);
        }

        public Player CreatePlayer(long playerId) => CreateFixturePlayer(playerId);

        // Building a player document needs no collection, which lets the scenario construct its fixtures
        // first and install this collection last, so the forwarder is the innermost override and the
        // shared static field is written once instead of being swapped again afterwards.
        public static Player CreateFixturePlayer(long playerId)
        {
            Player fixture = CreateDrawCompatibilityPlayer(playerId);
            // The document key is [BsonId] Player.Id (ObjectId); leaving it empty makes every
            // fixture collide, and it is unrelated to the numeric PlayerData.Id the harness sets.
            fixture.Id = ObjectId.GenerateNewId();
            return fixture;
        }

        // The recipient's applied watermark read from the committed document. Delivery resolves the defender
        // through its registered session when it has one and otherwise through the committed document, so
        // only the document is a reliable witness to what was actually applied.
        public int DurableWatermark(long defenderId, int seasonId, long attackerId) =>
            DurablePvp(defenderId).AppliedDefenseWatermarks
                .Where(entry => entry.SeasonId == seasonId && entry.AttackerId == attackerId)
                .Select(entry => entry.HighestAppliedBattleId).DefaultIfEmpty(-1).Max();

        public void Store(Player player) => proxy.Inner!.ReplaceOne(
            Builders<Player>.Filter.Eq(document => document.PlayerData.Id, player.PlayerData.Id), player,
            new ReplaceOptions { IsUpsert = true });

        // Fail the next durable save of one player document; zero arms nobody.
        public void ArmSaveFailure(long playerId) => proxy.FaultPlayerId = playerId;

        public void ClearSaveFailure() => proxy.FaultPlayerId = 0;

        // Tight fixture diagnostic against the exact predicates matchmaking applies: the authored season,
        // the seeded score and a complete saved defence. Exactly one document must match, and anything
        // else is reported with the observed count instead of surfacing later as an absent opponent.
        // The committed PvP state of a player, read straight from the fixture collection. Fixture players
        // hold their own in-memory copy, so only the document shows what the runtime actually persisted.
        public Theatre6PvpState DurablePvp(long playerId) => proxy.Inner!
            .Find(Builders<Player>.Filter.Eq(document => document.PlayerData.Id, playerId)).First().Theatre6.Pvp;

        public void RequireCandidateProfile(int seasonId, int score, int defenceSlots)
        {
            FilterDefinition<Player> filter = Builders<Player>.Filter.And(
                Builders<Player>.Filter.Eq(player => player.Theatre6.Pvp.AuthorizedSeasonId, seasonId),
                Builders<Player>.Filter.Eq(player => player.Theatre6.Pvp.InitializedSeasonId, seasonId),
                Builders<Player>.Filter.Eq(player => player.Theatre6.Pvp.Score, score),
                Builders<Player>.Filter.Size(player => player.Theatre6.Pvp.DefenseFiles, defenceSlots));
            long matching = proxy.Inner!.CountDocuments(filter);
            if (matching != 1)
                throw new InvalidDataException(
                    "The defence fixture must publish exactly one matchmaking candidate " +
                    $"(season {seasonId}, score {score}, {defenceSlots} defence slots), observed {matching}.");
        }

        public void Dispose()
        {
            MongoCollectionOverride.SetStaticField(field, original);
            try { proxy.Inner?.Database.DropCollection(proxy.Inner.CollectionNamespace.CollectionName); }
            catch (MongoException) { }
        }
    }

    private class RequiemSaveFaultProxy : DispatchProxy
    {
        public IMongoCollection<Player>? Inner { get; set; }
        public long FaultPlayerId;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is null || Inner is null)
                throw new InvalidOperationException("The forwarding players collection is not bound.");
            if (FaultPlayerId != 0 && args is not null
                && targetMethod.Name.StartsWith("ReplaceOne", StringComparison.Ordinal)
                && args.OfType<Player>().Any(document => document.PlayerData.Id == FaultPlayerId))
            {
                throw new MongoException($"Injected players save failure for player {FaultPlayerId}.");
            }

            return targetMethod.Invoke(Inner, args);
        }
    }

    // The defender's later session: its own player document, its own transport counter and its own
    // registered request path. It never shares the attacker's session or harness state.
    private sealed class PvpPeer : IDisposable
    {
        private LoopbackSessionHarness harness;
        private int packetCounter;
        public Player Player { get; private set; }
        public Session Session => harness.Session;

        public PvpPeer(string name, Player player, Theatre6PvpActivityTable season, int? rankId = null, int? score = null)
        {
            Player = player;
            harness = new LoopbackSessionHarness(CreateDrawCompatibilityCharacter(player.PlayerData.Id), player,
                CreateDrawCompatibilityInventory(player.PlayerData.Id, []), $"theatre6-{name}");
            Session.stage = CreateLoginAccountCompatibilityStage(player.PlayerData.Id);
            Theatre6PvpRankTable rank = rankId is int wanted
                ? TableReaderV2.Parse<Theatre6PvpRankTable>().Single(row => Convert.ToInt32(row.Id) == wanted)
                : TableReaderV2.Parse<Theatre6PvpRankTable>().OrderBy(row => Convert.ToInt32(row.Id))
                    .First(row => Convert.ToInt32(row.MaxScore) >= Convert.ToInt32(season.InitPoint));
            Player.Theatre6.Pvp.AuthorizedSeasonId = Convert.ToInt32(season.Id);
            Player.Theatre6.Pvp.AuthorizedTimeIds = [Convert.ToInt32(season.TimeId)];
            Player.Theatre6.Pvp.InitializedSeasonId = Convert.ToInt32(season.Id);
            Player.Theatre6.Pvp.Score = score ?? Convert.ToInt32(season.InitPoint);
            Player.Theatre6.Pvp.RankId = Convert.ToInt32(rank.Id);
            Player.Theatre6.Pvp.CurrentRankMinScore = Convert.ToInt32(rank.MinScore ?? 0);
            Player.Theatre6.Pvp.ActionPoint = Convert.ToInt32(TableReaderV2.Parse<Theatre6PvpConfigTable>()
                .Single(row => row.Key == "ActionPointInit").Values);
            int defenceSlots = RequiemDefenceSlots();
            Player.Theatre6.Files.AddRange(RequiemPvpArchives(defenceSlots));
            Player.Theatre6.Pvp.DefenseFiles = RequiemPvpArchives(defenceSlots);
            // The authored unlock condition Theatre6PvpConfig.UnlockPvpModeConditionId is the formula
            // 1021001&1021104: stage 2 cleared at least once, and at least two owned archives (the two
            // Files above). Both are seeded here so a focused defence scenario passes the real gate
            // instead of the gate being widened for the fixture.
            Player.Theatre6.PassStageRecords[2] = 1;
            Save();
        }

        // Rebuild this peer's session from the committed document, the shape RequiemCase.Relog uses. The
        // runtime resolves an offline defender's delivery through the committed document, so a fixture that
        // holds its own object has to re-read that document before it acts: otherwise it asserts against a
        // stale mirror, and its next save would write the mirror back over the delivered watermark, score and
        // records.
        public void ReloadFromCommitted()
        {
            Player committed = Player.TryFromPlayerId(Player.PlayerData.Id)
                ?? throw new InvalidDataException($"Theatre6 fixture: player {Player.PlayerData.Id} has no committed document to reload.");
            harness.Dispose();
            harness = new LoopbackSessionHarness(CreateDrawCompatibilityCharacter(committed.PlayerData.Id), committed,
                CreateDrawCompatibilityInventory(committed.PlayerData.Id, []), $"theatre6-peer-reload-{committed.PlayerData.Id}");
            Session.stage = CreateLoginAccountCompatibilityStage(committed.PlayerData.Id);
            packetCounter = 0;
            Player = committed;
        }

        public void Reconcile(DateTimeOffset now) => RequiredMethod(RequiredAscNetGameServerType("AscNet.GameServer.Handlers.Theatre6Module"),
            "ReconcileAvailability", BindingFlags.Static | BindingFlags.NonPublic,
            [typeof(Player), typeof(DateTimeOffset)]).Invoke(null, [Player, now]);

        public void Save()
        {
            Player.SaveChecked();
            Session.character.SaveChecked();
            Session.inventory.SaveChecked();
        }

        public int Score() => Player.Theatre6.Pvp.Score;

        public byte[] Document() => Player.ToBson();

        public List<Theatre6DefenseOutcome> PendingOutboxes() => Player.Theatre6.Pvp.PendingDefenseOutcomes;

        public int Watermark(int seasonId, long attackerId) => Player.Theatre6.Pvp.AppliedDefenseWatermarks
            .Where(entry => entry.SeasonId == seasonId && entry.AttackerId == attackerId)
            .Select(entry => entry.HighestAppliedBattleId).DefaultIfEmpty(-1).Max();

        public List<object?> ArchiveSlots() => Player.Theatre6.Pvp.DefenseFiles
            .Select(file => (object?)Req(("CharacterId", file.CharacterId), ("SlotId", file.SlotId))).ToList();

        // The authored environment buff of the held rank; rank one legitimately has none.
        public int DefenseBuff()
        {
            Theatre6PvpRankTable? rank = TableReaderV2.Parse<Theatre6PvpRankTable>()
                .FirstOrDefault(row => Convert.ToInt32(row.Id) == Player.Theatre6.Pvp.RankId && Convert.ToInt32(row.PvpBuffGroupId ?? 0) > 0);
            if (rank is null) return 0;
            Theatre6PvpBuffGroupTable group = TableReaderV2.Parse<Theatre6PvpBuffGroupTable>()
                .Single(row => Convert.ToInt32(row.Id) == rank.PvpBuffGroupId);
            return group.DefBuffs.Count > 0 ? group.DefBuffs[0] : 0;
        }

        public JObject Call(string requestName, object? request = null, bool? success = true)
        {
            int id = ++packetCounter;
            harness.WriteClientBytes(LoopbackSessionHarness.SerializeClientRequestFrame(requestName, id, request));
            for (int index = 0; index < 128; index++)
            {
                Packet packet = harness.ReadPacket($"{requestName} peer result {index}");
                if (packet.Type == Packet.ContentType.Push)
                    continue;
                AssertEqual(Packet.ContentType.Response, packet.Type, $"{requestName} peer packet type");
                Packet.Response response = MessagePackSerializer.Deserialize<Packet.Response>(packet.Content);
                AssertEqual(id, response.Id, $"{requestName} peer correlation");
                AssertEqual(requestName[..^"Request".Length] + "Response", response.Name, $"{requestName} peer response name");
                JObject body = JObject.Parse(MessagePackSerializer.ConvertToJson(response.Content));
                if (success.HasValue)
                    AssertEqual(success.Value, body.Value<int>("Code") == 0, $"{requestName} peer success={success}, body={body}");
                return body;
            }

            throw new InvalidDataException($"{requestName}: peer response missing");
        }

        public void Dispose() => harness.Dispose();
    }

    // An authored rank profile whose RobotProp is zero offers only saved defences, so a defensive
    // opponent is guaranteed to be offered without altering the table or the runtime weights. Every
    // lower authored rank carries a robot probability, which would make the offer a coin flip.
    private static Theatre6PvpRankTable RequiemHumanOnlyRank() => TableReaderV2.Parse<Theatre6PvpRankTable>()
        .Where(row => (row.RobotProp ?? 0) == 0)
        .OrderBy(row => Convert.ToInt32(row.Id))
        .First();

    // The authored number of defence slots a saved defence must fill; the same value gates the
    // client's own defence confirm button.
    // Attack positions the client fills before it will start a Phantom Clash attack; the server requires
    // exactly this many lineup entries.

    private static int RequiemAttackPositions() => 3;

    private static int RequiemDefenceSlots() => Convert.ToInt32(TableReaderV2.Parse<Theatre6PvpConfigTable>()
        .Single(row => row.Key == "MaxSlotDefenseLineupLimit").Values);

    // A whole-player document with only the request-receipt ledger removed. The ledger IS the dedupe
    // mechanism, so it necessarily advances whenever a request is served, including a replayed one; every
    // other field — score, records, watermark, defence, progression — must be identical between the
    // original and the replay. That is what lets this comparison catch a double-apply instead of masking
    // one, which a raw whole-document byte comparison cannot do because it also trips on the ledger.
    internal static string DocumentWithoutReceipts(byte[] document)
    {
        BsonDocument parsed = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<BsonDocument>(document);
        if (parsed.TryGetValue("theatre6", out BsonValue? mode) && mode.IsBsonDocument)
        {
            BsonDocument theatre6 = mode.AsBsonDocument;
            theatre6.Remove("next_mutation_id");
            theatre6.Remove("receipt_session");
            theatre6.Remove("receipt_high_water");
            theatre6.Remove("request_receipts");
        }
        return Convert.ToHexString(parsed.ToBson());
    }

    // Authored Theatre6 archives for cross-player fixtures: the table-selected build's character,
    // fashion, attributes and base skills, scored with the authored stage floor.
    private static List<Theatre6FileState> RequiemPvpArchives(int count)
    {
        (int character, int fashion, int _, int difficulty) = RequiemBuild(0, 0);
        Theatre6CharacterTable row = TableReaderV2.Parse<Theatre6CharacterTable>()
            .Single(candidate => Convert.ToInt32(candidate.Id) == character);
        List<Theatre6AttrState> attrs = [];
        for (int index = 0; index < row.AttrValue.Count && index < 5; index++)
            if (row.AttrValue[index] > 0) attrs.Add(new Theatre6AttrState { AttrId = index + 1, Value = row.AttrValue[index] });
        List<Theatre6SkillState> skills = [];
        for (int index = 0; index < row.BaseSkill.Count; index++)
            skills.Add(new Theatre6SkillState { SlotType = 2, Position = index + 1, SkillId = row.BaseSkill[index] });
        int score = Convert.ToInt32(TableReaderV2.Parse<Theatre6StageTable>()
            .Single(stage => Convert.ToInt32(stage.Id) == RequiemDifficultyStage(difficulty)).MinScores[0]);
        List<Theatre6FileState> files = [];
        for (int slot = 1; slot <= count; slot++)
        {
            files.Add(new Theatre6FileState
            {
                SlotId = slot, CharacterId = character, FashionId = fashion, Score = score,
                Attrs = [.. attrs], Skills = [.. skills]
            });
        }

        return files;
    }

    // JToken -> plain CLR values so a recovered wire snapshot can be re-serialized as a request.
    // Integer-keyed maps (PlayerSeeds, Attribs, MagicIds, PvpBuffActionRecord) must stay integer
    // keyed: the server's typed DTOs cannot deserialize string keys into Dictionary<int,...>.
    private static object? RequiemPlain(JToken? token) => token switch
    {
        null => null,
        JObject obj => RequiemPlainObject(obj),
        JArray array => array.Select(RequiemPlain).ToList(),
        JValue value => value.Value,
        _ => token.ToString()
    };

    private static object RequiemPlainObject(JObject obj) => obj.Properties().All(property => int.TryParse(property.Name, out _))
        ? obj.Properties().ToDictionary(property => int.Parse(property.Name), property => RequiemPlain(property.Value))
        : obj.Properties().ToDictionary(property => property.Name, property => RequiemPlain(property.Value));
}
