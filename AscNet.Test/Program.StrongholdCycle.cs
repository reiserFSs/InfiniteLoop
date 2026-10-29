using System.Reflection;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.Table.V2.share.condition;
using AscNet.Table.V2.share.config;
using AscNet.Table.V2.share.fuben.stronghold;
using MessagePack;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace AscNet.Test;

internal partial class Program
{
    // Norman Revival Plan: daily settlement (endurance/electric/mining), mineral claim, per-cycle achievement claims, stay.
    private static void ValidateStrongholdCycle()
    {
        PacketFactory.LoadPacketHandlers();
        using MongoCollectionOverride stageMongo = MongoCollectionOverride.InstallForStudyProgressionCompatibility(out _);
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(out _, out _, out _);
        Type module = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.StrongholdModule");
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        MethodInfo reconcile = RequiredMethod(module, "Reconcile", flags, [typeof(Session), typeof(DateTimeOffset), typeof(bool)]);
        MethodInfo startCycle = RequiredMethod(module, "StartCycle", flags,
            [typeof(StrongholdState), typeof(int), typeof(long), typeof(StrongholdLevelTable), typeof(StrongholdActivityTable)]);

        StrongholdActivityTable activity = TableReaderV2.Parse<StrongholdActivityTable>().Single(row => row.Id == 1);
        StrongholdLevelTable level = TableReaderV2.Parse<StrongholdLevelTable>().Single(row => row.Id == 1);
        var enduranceRows = TableReaderV2.Parse<StrongholdEnduranceTable>().ToDictionary(row => row.Id);
        var electricRows = TableReaderV2.Parse<StrongholdElectricTable>().ToDictionary(row => row.Id);
        int Cfg(string key) => TableReaderV2.Parse<StrongholdCfgTable>().FirstOrDefault(row => row.Key == key)?.Value ?? 0;
        int minerId = Cfg("MinerId"), mineralId = Cfg("MineralId"), efficiency = Cfg("MinerEfficiency"), grow = Cfg("MinerGrowRate");
        long dailyReset = long.Parse(TableReaderV2.Parse<ConfigTable>().Single(row => row.Key == "DailyResetTimestamp").Value);
        long nowSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long uid = 49_300;

        (Player player, LoopbackSessionHarness harness) Create(long begin, int miners = 0)
        {
            uid++;
            Character roster = CreateDrawCompatibilityCharacter(uid);
            roster.Characters = [new CharacterData { Id = 1_021_001 }];
            Player player = CreateDrawCompatibilityPlayer(uid);
            player.PlayerData.Level = 80;
            StrongholdState state = player.Stronghold;
            state.ActivityId = 1;
            state.BeginTime = checked((uint)begin);
            state.FightBeginTime = checked((int)begin);
            state.LevelId = level.Id;
            state.CurDay = 1;
            state.SettledPeriod = (begin - dailyReset) / 86400;
            state.Endurance = level.InitEndurance;
            state.ElectricEnergy = level.InitElectricEnergy;
            List<Item> items = miners > 0 ? [new Item { Id = minerId, Count = miners }] : [];
            LoopbackSessionHarness harness = new(roster, player, CreateDrawCompatibilityInventory(uid, items), $"norman-cycle-{uid}");
            harness.Session.stage = CreateLoginAccountCompatibilityStage(uid);
            return (player, harness);
        }
        void Reconcile(LoopbackSessionHarness h, long now) =>
            reconcile.Invoke(null, [h.Session, DateTimeOffset.FromUnixTimeSeconds(now), true]);
        void ExpectPush(LoopbackSessionHarness h, string name)
        {
            for (int i = 0; i < 8; i++)
            {
                Packet packet = h.ReadPacket("Stronghold day push");
                if (packet.Type == Packet.ContentType.Push
                    && MessagePackSerializer.Deserialize<Packet.Push>(packet.Content).Name == name) return;
            }
            throw new InvalidDataException($"{name} was not pushed.");
        }
        string Snapshot(Player player) => Convert.ToHexString(player.Stronghold.ToBson());
        // Day boundaries are the authored reset, not UTC midnight: the first one after `begin`.
        long begin0 = 1_800_000_000 - ((1_800_000_000 - dailyReset) % 86400) + 3600;
        long firstBoundary = begin0 - ((begin0 - dailyReset) % 86400) + 86400;

        // 1. Day advance applies the table amounts once, for two different missed-day counts.
        foreach (int missed in new[] { 1, 3 })
        {
            const int startMiners = 2;
            var (player, h) = Create(begin0, startMiners);
            using (h)
            {
                long now = begin0 + missed * 86400L;
                Reconcile(h, now);
                ExpectPush(h, nameof(NotifyStrongholdChangeDay));
                StrongholdState state = player.Stronghold;
                int expectedEndurance = level.InitEndurance, expectedElectric = level.InitElectricEnergy;
                long miners = startMiners;
                int mineral = 0;
                for (int day = 1; day <= missed; day++)
                {
                    expectedEndurance += enduranceRows[day].Endurance[level.Id - 1];
                    expectedElectric += electricRows[day].ElectricEnerge[level.Id - 1];
                    AssertEqual(miners, state.MineRecords[day - 1].MinerCount, $"{missed} missed: day {day} miner count");
                    AssertEqual(miners * efficiency, state.MineRecords[day - 1].MineralCount, $"{missed} missed: day {day} mineral");
                    mineral += (int)(miners * efficiency);
                    miners = (miners * (100 + grow) + 99) / 100;
                }
                AssertEqual(1 + missed, state.CurDay, $"{missed} missed: CurDay advances");
                AssertEqual(expectedEndurance, state.Endurance, $"{missed} missed: endurance table amounts");
                AssertEqual(expectedElectric, state.ElectricEnergy, $"{missed} missed: electric table amounts");
                AssertEqual(missed, state.MineRecords.Count, $"{missed} missed: one mine record per day");
                AssertEqual(mineral, state.MineralLeft, $"{missed} missed: MineralLeft");
                AssertEqual(mineral, state.TotalMineral, $"{missed} missed: TotalMineral");
                AssertEqual(miners, h.Session.inventory.SpendableCount(minerId), $"{missed} missed: miners grew by MinerGrowRate");
                string settled = Snapshot(player);
                long minerCount = h.Session.inventory.SpendableCount(minerId);
                Reconcile(h, now);
                AssertEqual(settled, Snapshot(player), $"{missed} missed: reconciling the same clock settles nothing twice");
                AssertEqual(minerCount, h.Session.inventory.SpendableCount(minerId), $"{missed} missed: miners are not grown twice");
                Player reloaded = BsonSerializer.Deserialize<Player>(player.ToBson());
                AssertEqual(settled, Snapshot(reloaded), $"{missed} missed: settled day survives BSON reload");
                AssertEqual(state.SettledPeriod, reloaded.Stronghold.SettledPeriod, $"{missed} missed: settled period persists");
            }
        }

        // 2. The boundary is the authored daily reset.
        {
            var (player, h) = Create(begin0, 1);
            using (h)
            {
                Reconcile(h, firstBoundary - 1);
                AssertEqual(1, player.Stronghold.CurDay, "no day before the authored reset");
                Reconcile(h, firstBoundary);
                AssertEqual(2, player.Stronghold.CurDay, "day advances exactly at the authored reset");
            }
        }

        // 3. A paused day holds its boundary (nothing settled), then settles on the next one.
        {
            var (player, h) = Create(begin0, 1);
            using (h)
            {
                player.Stronghold.StayDays = [1];
                Reconcile(h, begin0 + 86400);
                AssertEqual(1, player.Stronghold.CurDay, "paused day does not advance");
                AssertEqual(level.InitEndurance, player.Stronghold.Endurance, "paused day adds no endurance");
                AssertEqual(0, player.Stronghold.MineRecords.Count, "paused day does not mine");
                AssertEqual(0, player.Stronghold.StayDays.Count, "pause is consumed by its boundary");
                Reconcile(h, begin0 + 2 * 86400L);
                AssertEqual(2, player.Stronghold.CurDay, "day after a pause settles");
                AssertEqual(level.InitEndurance + enduranceRows[1].Endurance[level.Id - 1], player.Stronghold.Endurance,
                    "only the unpaused boundary adds day 1 endurance");
            }
        }

        // 4. Mineral claim credits the item once; a retry neither doubles nor loses.
        foreach (int claimed in new[] { 40, 135 })
        {
            var (player, h) = Create(nowSeconds);
            using (h)
            {
                player.Stronghold.MineralLeft = claimed;
                player.Stronghold.TotalMineral = claimed + 7;
                InvokeRegisteredRequestHandler("GetStrongholdMineralRequest", h.Session, 49_401, new GetStrongholdMineralRequest());
                GetStrongholdMineralResponse first = ReadStrongholdResponse<GetStrongholdMineralResponse>(h, 49_401, out _);
                AssertEqual(0, first.Code, $"{claimed}: mineral claim succeeds");
                AssertEqual(claimed, first.MineralCount, $"{claimed}: mineral claim reports amount");
                AssertEqual(claimed, h.Session.inventory.SpendableCount(mineralId), $"{claimed}: mineral item credited");
                AssertEqual(0, player.Stronghold.MineralLeft, $"{claimed}: MineralLeft cleared by the claim");
                AssertEqual(claimed + 7, player.Stronghold.TotalMineral, $"{claimed}: TotalMineral untouched by the claim");
                InvokeRegisteredRequestHandler("GetStrongholdMineralRequest", h.Session, 49_402, new GetStrongholdMineralRequest());
                AssertEqual(20113018, ReadStrongholdResponse<GetStrongholdMineralResponse>(h, 49_402, out _).Code,
                    $"{claimed}: empty claim rejected");
                // Claim saved the item but the player save was lost: the same MineralLeft comes back.
                player.Stronghold.MineralLeft = claimed;
                InvokeRegisteredRequestHandler("GetStrongholdMineralRequest", h.Session, 49_403, new GetStrongholdMineralRequest());
                AssertEqual(0, ReadStrongholdResponse<GetStrongholdMineralResponse>(h, 49_403, out _).Code, $"{claimed}: retry completes");
                AssertEqual(claimed, h.Session.inventory.SpendableCount(mineralId), $"{claimed}: retry does not double the item");
                AssertEqual(0, player.Stronghold.MineralLeft, $"{claimed}: retry clears MineralLeft");
                AssertEqual(0, BsonSerializer.Deserialize<Player>(player.ToBson()).Stronghold.MineralLeft, $"{claimed}: cleared claim persists");
            }
        }

        // 5. Achievement rewards are earned again each cycle, never twice within one.
        {
            Dictionary<int, ConditionTable> conditions = TableReaderV2.Parse<ConditionTable>().ToDictionary(row => row.Id);
            StrongholdRewardTable reward = TableReaderV2.Parse<StrongholdRewardTable>()
                .First(row => row.LevelId == level.Id && conditions[row.Condition].Type == 10131);
            int groupId = conditions[reward.Condition].Params[0];
            var (player, h) = Create(nowSeconds);
            using (h)
            {
                int ClaimCode(int packetId)
                {
                    InvokeRegisteredRequestHandler("GetStrongholdRewardRequest", h.Session, packetId, new GetStrongholdRewardRequest { Ids = [reward.Id] });
                    return ReadStrongholdResponse<GetStrongholdRewardResponse>(h, packetId, out _).Code;
                }
                string KeyFor(StrongholdState state) =>
                    $"stronghold:{uid}:{state.ActivityId}:{state.BeginTime}:achievement:{reward.Id}";
                player.Stronghold.FinishGroupIds = [groupId];
                AssertEqual(0, ClaimCode(49_410), "cycle 1 achievement claimable");
                string cycle1Key = KeyFor(player.Stronghold);
                AssertEqual(true, h.Session.inventory.AppliedRewardClaims.Contains(cycle1Key), "cycle 1 claim key is scoped to the cycle");
                AssertEqual(20113018, ClaimCode(49_411), "cycle 1 achievement not claimable twice");
                AssertEqual(true, BsonSerializer.Deserialize<Player>(player.ToBson()).Stronghold.ClaimedRewardIds.Contains(reward.Id),
                    "cycle 1 claim persists");

                startCycle.Invoke(null, [player.Stronghold, player.Stronghold.ActivityId + 1, nowSeconds + 60, level, activity]);
                AssertEqual(0, player.Stronghold.RewardIds.Count + player.Stronghold.ClaimedRewardIds.Count, "new cycle clears claimed achievements");
                // Cycle 2 begins in the future; the claim handler does not check the window.
                player.Stronghold.FinishGroupIds = [groupId];
                AssertEqual(0, ClaimCode(49_412), "cycle 2 achievement claimable again");
                AssertEqual(true, KeyFor(player.Stronghold) != cycle1Key, "cycle 2 claim key differs from cycle 1");
                AssertEqual(true, h.Session.inventory.AppliedRewardClaims.Contains(KeyFor(player.Stronghold)), "cycle 2 claim recorded under its own key");
                AssertEqual(20113018, ClaimCode(49_413), "cycle 2 achievement not claimable twice");
            }
        }

        // 6. Stay records the reconciled day idempotently and never moves CurDay.
        foreach (int curDay in new[] { 1, 4 })
        {
            var (player, h) = Create(nowSeconds);
            using (h)
            {
                player.Stronghold.CurDay = curDay;
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    InvokeRegisteredRequestHandler("SetStrongholdStayRequest", h.Session, 49_420 + attempt, new SetStrongholdStayRequest());
                    SetStrongholdStayResponse stay = ReadStrongholdResponse<SetStrongholdStayResponse>(h, 49_420 + attempt, out _);
                    AssertEqual(true, stay.StayDays.SequenceEqual([curDay]), $"day {curDay} stay attempt {attempt} records the current day once");
                    AssertEqual(curDay, player.Stronghold.CurDay, $"day {curDay} stay attempt {attempt} keeps CurDay");
                }
                Player reloaded = BsonSerializer.Deserialize<Player>(player.ToBson());
                AssertEqual(true, reloaded.Stronghold.StayDays.SequenceEqual([curDay]) && reloaded.Stronghold.CurDay == curDay,
                    $"day {curDay} stay persists");
            }
        }
    }
}
