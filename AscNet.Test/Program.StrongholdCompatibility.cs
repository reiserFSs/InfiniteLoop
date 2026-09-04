using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.GameServer;
using AscNet.GameServer.Handlers;
using AscNet.Common.Util;
using MongoDB.Bson;
using MessagePack;
using AscNet.Table.V2.share.condition;
using AscNet.Table.V2.share.fuben.stronghold;

namespace AscNet.Test;

internal partial class Program
{
    private static void ValidateStrongholdCompatibility()
    {
        PacketFactory.LoadPacketHandlers();
        const long uid = 48_801;
        Character roster = CreateDrawCompatibilityCharacter(uid);
        roster.Characters = [new CharacterData { Id = 1_021_001 }, new CharacterData { Id = 1_021_002 }, new CharacterData { Id = 1_021_003 }];
        Player player = CreateDrawCompatibilityPlayer(uid);
        player.PlayerData.Level = 80;
        Player secondPlayer = CreateDrawCompatibilityPlayer(uid + 1);
        secondPlayer.PlayerData.Level = 80;
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(out _, out _, out _);
        using LoopbackSessionHarness h = new(roster, player, CreateDrawCompatibilityInventory(uid, []), "stronghold-loopback");
        h.Session.stage = CreateLoginAccountCompatibilityStage(uid);

        void Call<T>(string requestName, int id, object request, string responseName) where T : class
        {
            InvokeRegisteredRequestHandler(requestName, h.Session, id, request);
            _ = ReadResponsePayload<T>(h, id, responseName, requestName);
        }

        Type module = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.StrongholdModule");
        player.Stronghold.ActivityId = 1;
        player.Stronghold.BeginTime = 1;
        secondPlayer.Stronghold.ActivityId = 1;
        secondPlayer.Stronghold.BeginTime = 1;
        AssertEqual(true, player.Stronghold.ActivityId > 0 && player.Stronghold.BeginTime > 0, "Stronghold test state is open");
        AssertEqual(player.Stronghold.ActivityId, secondPlayer.Stronghold.ActivityId, "Players select the same authoritative activity");
        AssertEqual(true, player.Stronghold.BeginTime != 0 && secondPlayer.Stronghold.BeginTime != 0, "Players activate independently");

        Call<GetStrongholdMineralResponse>("GetStrongholdMineralRequest", 48_811, new GetStrongholdMineralRequest(), nameof(GetStrongholdMineralResponse));
        Call<SetStrongholdElectricTeamResponse>("SetStrongholdElectricTeamRequest", 48_812, new SetStrongholdElectricTeamRequest { CharacterIds = [1_021_001] }, nameof(SetStrongholdElectricTeamResponse));
        Call<ResetStrongholdGroupResponse>("ResetStrongholdGroupRequest", 48_813, new ResetStrongholdGroupRequest { Id = -1 }, nameof(ResetStrongholdGroupResponse));
        Call<ResetStrongholdStageResponse>("ResetStrongholdStageRequest", 48_814, new ResetStrongholdStageRequest { GroupId = -1, StageId = -1 }, nameof(ResetStrongholdStageResponse));
        Call<SetStrongholdTeamResponse>("SetStrongholdTeamRequest", 48_815, new SetStrongholdTeamRequest { Own = true, TeamInfos = [new StrongholdTeamInfo { Id = 1, CharacterInfos = [new StrongholdCharacterInfo { Id = 1_021_001, Pos = 1 }] }] }, nameof(SetStrongholdTeamResponse));
        Call<SetStrongholdFightTeamResponse>("SetStrongholdFightTeamRequest", 48_816, new SetStrongholdFightTeamRequest { Id = -1 }, nameof(SetStrongholdFightTeamResponse));
        Call<GetStrongholdAssistCharacterListResponse>("GetStrongholdAssistCharacterListRequest", 48_817, new GetStrongholdAssistCharacterListRequest(), nameof(GetStrongholdAssistCharacterListResponse));
        Call<SetStrongholdAssistCharacterResponse>("SetStrongholdAssistCharacterRequest", 48_818, new SetStrongholdAssistCharacterRequest { CharacterId = 1_021_001 }, nameof(SetStrongholdAssistCharacterResponse));
        Call<GetStrongholdLendDetailResponse>("GetStrongholdLendDetailRequest", 48_819, new GetStrongholdLendDetailRequest(), nameof(GetStrongholdLendDetailResponse));
        int levelId = TableReaderV2.Parse<AscNet.Table.V2.share.fuben.stronghold.StrongholdLevelTable>()
            .Where(row => player.PlayerData.Level >= row.MinLevel && player.PlayerData.Level <= row.MaxLevel)
            .Select(row => row.Id).First();
        Call<SelectStrongholdLevelResponse>("SelectStrongholdLevelRequest", 48_822,
            new SelectStrongholdLevelRequest { LevelId = levelId }, nameof(SelectStrongholdLevelResponse));
        List<AscNet.Table.V2.share.fuben.stronghold.StrongholdGroupTable> groupRows =
            TableReaderV2.Parse<AscNet.Table.V2.share.fuben.stronghold.StrongholdGroupTable>();
        int groupId = player.Stronghold.GroupStageDatas
            .First(value => value.StageIds.Count > 1
                && groupRows.Single(row => row.Id == value.Id).PreId is not > 0).Id;
        int lockedGroupId = player.Stronghold.GroupStageDatas
            .First(value => groupRows.Single(row => row.Id == value.Id).PreId is > 0).Id;
        StrongholdGroupStageData groupStages = player.Stronghold.GroupStageDatas.Single(value => value.Id == groupId);
        StrongholdGroupStageData lockedGroupStages = player.Stronghold.GroupStageDatas.Single(value => value.Id == lockedGroupId);
        uint firstStage = groupStages.StageIds[0];
        uint lockedStage = lockedGroupStages.StageIds[0];

        byte[] beforeSweep = player.ToBson();
        InvokeRegisteredRequestHandler(nameof(SweepStrongholdStageRequest), h.Session, 48_823,
            new SweepStrongholdStageRequest { GroupId = groupId });
        AssertEqual(true, ReadResponsePayload<SweepStrongholdStageResponse>(
            h, 48_823, nameof(SweepStrongholdStageResponse), "Stronghold sweep before normal clear").Code != 0,
            "Stronghold sweep rejects an uncleared stage");
        AssertEqual(Convert.ToHexString(beforeSweep), Convert.ToHexString(player.ToBson()),
            "Stronghold sweep rejection does not mutate state");

        Call<SetStrongholdFightTeamResponse>("SetStrongholdFightTeamRequest", 48_824,
            new SetStrongholdFightTeamRequest
            {
                Id = lockedGroupId,
                TeamInfos = [new StrongholdTeamInfo
                {
                    Id = 1,
                    CharacterInfos = [new StrongholdCharacterInfo { Id = 1_021_001, Pos = 1 }]
                }]
            }, nameof(SetStrongholdFightTeamResponse));
        byte[] beforePrerequisite = player.ToBson();
        InvokeRegisteredRequestHandler(nameof(PreFightRequest), h.Session, 48_825,
            new PreFightRequest { PreFightData = new() { StageId = lockedStage, CardIds = [1_021_001], CaptainPos = 1, FirstFightPos = 1 } });
        AssertEqual(true, ReadResponsePayload<PreFightResponse>(
            h, 48_825, nameof(PreFightResponse), "Stronghold locked-group pre-fight").Code != 0,
            "Stronghold pre-fight rejects a group whose table predecessor is uncleared");
        AssertEqual(Convert.ToHexString(beforePrerequisite), Convert.ToHexString(player.ToBson()),
            "Stronghold prerequisite rejection does not mutate state");
        AssertEqual(null, h.Session.fight, "Stronghold prerequisite rejection does not start a fight");

        Call<SetStrongholdFightTeamResponse>("SetStrongholdFightTeamRequest", 48_826,
            new SetStrongholdFightTeamRequest
            {
                Id = groupId,
                TeamInfos = [new StrongholdTeamInfo
                {
                    Id = 1,
                    CharacterInfos = [new StrongholdCharacterInfo { Id = 1_021_001, Pos = 1 }]
                }]
            }, nameof(SetStrongholdFightTeamResponse));
        InvokeRegisteredRequestHandler(nameof(PreFightRequest), h.Session, 48_827,
            new PreFightRequest { PreFightData = new() { StageId = firstStage, CardIds = [1_021_001], CaptainPos = 1, FirstFightPos = 1 } });
        PreFightResponse firstPreFight = ReadResponsePayload<PreFightResponse>(
            h, 48_827, nameof(PreFightResponse), "Stronghold normal pre-fight");
        AssertEqual(0, firstPreFight.Code, "Stronghold normal pre-fight code");
        InvokeRegisteredRequestHandler(nameof(FightSettleRequest), h.Session, 48_828,
            CreateMissingStageSettleRequest(firstStage, firstPreFight.FightData.FightId, uid));
        FightSettleResponse firstSettle = ReadResponsePayload<FightSettleResponse>(
            h, 48_828, nameof(FightSettleResponse), "Stronghold normal settle");
        AssertEqual(0, firstSettle.Code, "Stronghold normal settle code");
        AssertEqual(true, player.Stronghold.GroupInfos.Single(value => value.Id == groupId).FinishStageIds.Contains((int)firstStage),
            "Stronghold normal settle persists the exact stage clear");
        AssertEqual(true, player.Stronghold.PendingStageId > 0,
            "Stronghold normal settle preserves the next selectable stage");

        int sweepPacketId = 48_829;
        while (!player.Stronghold.FinishGroupIds.Contains(groupId))
        {
            InvokeRegisteredRequestHandler(nameof(SweepStrongholdStageRequest), h.Session, sweepPacketId++,
                new SweepStrongholdStageRequest { GroupId = groupId });
            SweepStrongholdStageResponse sweep = ReadResponsePayload<SweepStrongholdStageResponse>(
                h, sweepPacketId - 1, nameof(SweepStrongholdStageResponse), "Stronghold sweep after normal clear");
            AssertEqual(0, sweep.Code, "Stronghold sweep after normal clear code");
        }
        AssertEqual(true, player.Stronghold.FinishGroupIds.Contains(groupId),
            "Stronghold sweep completes the remaining table stages");
        byte[] beforeDuplicateSweep = player.ToBson();
        InvokeRegisteredRequestHandler(nameof(SweepStrongholdStageRequest), h.Session, sweepPacketId++,
            new SweepStrongholdStageRequest { GroupId = groupId });
        AssertEqual(true, ReadResponsePayload<SweepStrongholdStageResponse>(
            h, sweepPacketId - 1, nameof(SweepStrongholdStageResponse), "Stronghold duplicate sweep").Code != 0,
            "Stronghold duplicate sweep rejects a completed group");
        AssertEqual(Convert.ToHexString(beforeDuplicateSweep), Convert.ToHexString(player.ToBson()),
            "Stronghold duplicate sweep does not mutate state");
        int rewardClaims = h.Session.inventory.AppliedRewardClaims.Count(key =>
            key.StartsWith($"stronghold:{uid}:{player.Stronghold.ActivityId}:{groupId}", StringComparison.Ordinal));
        AssertEqual(1, rewardClaims, "Stronghold group reward is applied once");
        byte[] finishedState = player.ToBson();
        NotifyStrongholdLoginData settledLogin = (NotifyStrongholdLoginData)RequiredMethod(module, "BuildLoginData",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic, [typeof(Player)]).Invoke(null, [player])!;
        AssertEqual(true, settledLogin.FinishGroupIds.Contains(groupId), "Stronghold relogin exposes the finished group");
        Player reloadedSettlement = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<Player>(finishedState);
        AssertEqual(true, reloadedSettlement.Stronghold.FinishGroupIds.Contains(groupId),
            "Stronghold finished group persists through relogin");
        AssertEqual(finishedState.Length, reloadedSettlement.ToBson().Length, "Stronghold relogin state round-trips");

        List<ConditionTable> rewardConditions = TableReaderV2.Parse<ConditionTable>();
        List<(StrongholdRewardTable reward, ConditionTable condition)> claimRows = TableReaderV2.Parse<StrongholdRewardTable>()
            .Where(row => row.LevelId == player.Stronghold.LevelId)
            .Join(rewardConditions.Where(condition => condition.Type is 10131 or 12103),
                reward => reward.Condition, condition => condition.Id, (reward, condition) => (reward, condition))
            .GroupBy(pair => pair.condition.Type is 10131 ? pair.condition.Type : pair.condition.Id)
            .Select(group => group.First())
            .ToList();
        AssertEqual(3, claimRows.Count, "Stronghold reward fixture covers 10131 and two 12103 rows");
        int rewardPacketId = 48_840;
        (StrongholdRewardTable reward, ConditionTable condition) row10131 =
            claimRows.Single(pair => pair.condition.Type == 10131);
        List<(StrongholdRewardTable reward, ConditionTable condition)> energyRows =
            claimRows.Where(pair => pair.condition.Type == 12103).ToList();
        AssertEqual(2, energyRows.Count, "Stronghold reward fixture covers two distinct 12103 conditions");
        (StrongholdRewardTable reward, ConditionTable condition) missingEnergy = energyRows[0];
        (StrongholdRewardTable reward, ConditionTable condition) exactEnergy = energyRows[1];
        player.Stronghold.FinishGroupIds = claimRows
            .Select(pair => pair.condition.Params[0]).Distinct().ToList();
        player.Stronghold.FinishGroupInfos.Clear();
        player.Stronghold.HistoryFinishGroupInfos.Clear();
        StrongholdFinishGroupInfo exactInfo = new() { Id = exactEnergy.condition.Params[0] };
        if (exactEnergy.condition.Params[2] != 0)
            exactInfo.UsedSystemElectricEnergy = exactEnergy.condition.Params[1];
        else
            exactInfo.UsedElectricEnergy = exactEnergy.condition.Params[1];
        player.Stronghold.FinishGroupInfos.Add(exactInfo);

        player.Stronghold.FinishGroupIds.Remove(exactEnergy.condition.Params[0]);
        byte[] beforeStaleEnergy = player.ToBson();
        InvokeRegisteredRequestHandler(nameof(GetStrongholdRewardRequest), h.Session, rewardPacketId,
            new GetStrongholdRewardRequest { Ids = [exactEnergy.reward.Id] });
        AssertEqual(20113018, ReadResponsePayload<GetStrongholdRewardResponse>(
            h, rewardPacketId++, nameof(GetStrongholdRewardResponse), "Stronghold stale 12103 reward claim").Code,
            "Stronghold 12103 rejects stale finish info without finished-group membership");
        AssertEqual(Convert.ToHexString(beforeStaleEnergy), Convert.ToHexString(player.ToBson()),
            "Stronghold stale 12103 rejection is atomic");
        player.Stronghold.FinishGroupIds.Add(exactEnergy.condition.Params[0]);
        exactInfo.UsedSystemElectricEnergy = exactEnergy.condition.Params[2] != 0
            ? exactEnergy.condition.Params[1] + 1 : exactInfo.UsedSystemElectricEnergy;
        exactInfo.UsedElectricEnergy = exactEnergy.condition.Params[2] == 0
            ? exactEnergy.condition.Params[1] + 1 : exactInfo.UsedElectricEnergy;
        byte[] beforeOverThreshold = player.ToBson();
        InvokeRegisteredRequestHandler(nameof(GetStrongholdRewardRequest), h.Session, rewardPacketId,
            new GetStrongholdRewardRequest { Ids = claimRows.Select(pair => pair.reward.Id).ToList() });
        AssertEqual(20113018, ReadResponsePayload<GetStrongholdRewardResponse>(
            h, rewardPacketId++, nameof(GetStrongholdRewardResponse), "Stronghold over-threshold reward batch").Code,
            "Stronghold over-threshold 12103 rejects the whole reward batch");
        AssertEqual(Convert.ToHexString(beforeOverThreshold), Convert.ToHexString(player.ToBson()),
            "Stronghold over-threshold batch rejection is atomic");

        player.Stronghold.FinishGroupInfos.Clear();
        InvokeRegisteredRequestHandler(nameof(GetStrongholdRewardRequest), h.Session, rewardPacketId,
            new GetStrongholdRewardRequest { Ids = [missingEnergy.reward.Id] });
        GetStrongholdRewardResponse missingEnergyResponse = ReadResponsePayload<GetStrongholdRewardResponse>(
            h, rewardPacketId++, nameof(GetStrongholdRewardResponse), "Stronghold missing current energy reward");
        AssertEqual(0, missingEnergyResponse.Code, "Stronghold missing current 12103 info uses client sentinel");
        AssertEqual(true, player.Stronghold.ClaimedRewardIds.Contains(missingEnergy.reward.Id),
            "Stronghold missing current 12103 info claim persists");
        player.Stronghold.FinishGroupInfos.Add(exactInfo);
        if (exactEnergy.condition.Params[2] != 0)
            exactInfo.UsedSystemElectricEnergy = exactEnergy.condition.Params[1];
        else
            exactInfo.UsedElectricEnergy = exactEnergy.condition.Params[1];
        InvokeRegisteredRequestHandler(nameof(GetStrongholdRewardRequest), h.Session, rewardPacketId,
            new GetStrongholdRewardRequest { Ids = [row10131.reward.Id, exactEnergy.reward.Id] });
        GetStrongholdRewardResponse eligibleBatch = ReadResponsePayload<GetStrongholdRewardResponse>(
            h, rewardPacketId++, nameof(GetStrongholdRewardResponse), "Stronghold eligible reward batch");
        AssertEqual(0, eligibleBatch.Code, "Stronghold 10131 and exact 12103 eligible batch code");
        AssertEqual(true, eligibleBatch.SuccessIds.SequenceEqual([row10131.reward.Id, exactEnergy.reward.Id]),
            "Stronghold eligible reward batch success IDs");
        AssertEqual(true, player.Stronghold.ClaimedRewardIds.Contains(row10131.reward.Id)
            && player.Stronghold.ClaimedRewardIds.Contains(exactEnergy.reward.Id),
            "Stronghold eligible reward batch persists claimed IDs");
        int claimedRewardId = missingEnergy.reward.Id;
        int claimCount = h.Session.inventory.AppliedRewardClaims.Count(key =>
            key == $"stronghold:{uid}:achievement:{claimedRewardId}");
        byte[] beforeDuplicateClaim = player.ToBson();
        InvokeRegisteredRequestHandler(nameof(GetStrongholdRewardRequest), h.Session, rewardPacketId,
            new GetStrongholdRewardRequest { Ids = [claimedRewardId] });
        AssertEqual(20113018, ReadResponsePayload<GetStrongholdRewardResponse>(
            h, rewardPacketId++, nameof(GetStrongholdRewardResponse), "Stronghold duplicate reward claim").Code,
            "Stronghold duplicate reward claim uses retail rejection code");
        AssertEqual(claimCount, h.Session.inventory.AppliedRewardClaims.Count(key =>
            key == $"stronghold:{uid}:achievement:{claimedRewardId}"),
            "Stronghold duplicate reward claim does not grant twice");
        AssertEqual(Convert.ToHexString(beforeDuplicateClaim), Convert.ToHexString(player.ToBson()),
            "Stronghold duplicate reward claim does not mutate player state");
        Player reloadedRewards = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<Player>(player.ToBson());
        AssertEqual(true, claimRows.All(pair => reloadedRewards.Stronghold.ClaimedRewardIds.Contains(pair.reward.Id)),
            "Stronghold reward claims persist through relogin");

        NotifyStrongholdLoginData login = (NotifyStrongholdLoginData)RequiredMethod(module, "BuildLoginData", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic, [typeof(Player)]).Invoke(null, [player])!;
        AssertEqual(player.Stronghold.StayDays.Count, login.StayDays.Count, "Stronghold login state survives endpoint dispatch");
        AssertEqual(player.Stronghold.TeamInfos.Count, login.TeamInfos.Count, "Stronghold team projects on relogin");
        Player reloaded = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<Player>(player.ToBson());
        AssertEqual(player.Stronghold.LevelId, reloaded.Stronghold.LevelId, "Stronghold level persists through relogin");
        AssertEqual(player.Stronghold.PendingStageId, reloaded.Stronghold.PendingStageId, "Stronghold continuation persists through relogin");
        Player loginPlayer = CreateDrawCompatibilityPlayer(uid + 2);
        loginPlayer.PlayerData.Level = 80;
        loginPlayer.Stronghold.ActivityId = 1;
        loginPlayer.Stronghold.BeginTime = 1;
        loginPlayer.Stronghold.FightBeginTime = 1;
        loginPlayer.Stronghold.CurDay = 1;
        loginPlayer.Stronghold.LevelId = 1;
        loginPlayer.Stronghold.ElectricCharacterIds = [1_021_001];
        loginPlayer.Stronghold.LastResultRecord = new();
        using LoopbackSessionHarness loginHarness = new(
            CreateDrawCompatibilityCharacter(uid + 2),
            loginPlayer,
            CreateDrawCompatibilityInventory(uid + 2, []),
            "challenge-login-regression");
        loginHarness.Session.stage = CreateLoginAccountCompatibilityStage(uid + 2);
        System.Reflection.MethodInfo doLogin = RequiredMethod(
            RequiredAscNetGameServerType("AscNet.GameServer.Handlers.AccountModule"),
            "DoLogin",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic,
            [typeof(Session), typeof(bool)]);
        doLogin.Invoke(null, [loginHarness.Session, false]);

        _ = ReadPushPayload<NotifyLogin>(loginHarness, nameof(NotifyLogin), "challenge login NotifyLogin");
        string[] required = [
            nameof(NotifyArenaActivity),
            nameof(NotifyFubenBossSingleData),
            nameof(NotifyRepeatChallengeData),
            nameof(NotifyStrongholdLoginData),
            nameof(NotifyTransfiniteData)];
        HashSet<string> observed = [];
        Dictionary<string, int> positions = [];
        bool strongholdSeen = false;
        for (int index = 0; index < 128 && observed.Count < required.Length; index++)
        {
            Packet packet = loginHarness.ReadPacket($"challenge login startup {index + 1}");
            AssertEqual(Packet.ContentType.Push, packet.Type, $"challenge login startup {index + 1} packet type");
            Packet.Push push = MessagePackSerializer.Deserialize<Packet.Push>(packet.Content);
            if (push.Name == nameof(NotifyStrongholdLoginData))
            {
                strongholdSeen = true;
                NotifyStrongholdLoginData stronghold = MessagePackSerializer.Deserialize<NotifyStrongholdLoginData>(push.Content);
                AssertEqual(loginPlayer.Stronghold.ActivityId, stronghold.Id, "challenge login Stronghold activity id");
                AssertEqual(true, stronghold.BeginTime > 0 && stronghold.FightBeginTime > 0,
                    "challenge login Stronghold activation chronology");
                AssertEqual(true, loginPlayer.Stronghold.ElectricCharacterIds.SequenceEqual(stronghold.ElectricCharacterIds),
                    "challenge login Stronghold electric team");
                AssertEqual(true, stronghold.ElectricCharacterIds is not null
                    && stronghold.FinishGroupIds is not null
                    && stronghold.FinishGroupInfos is not null
                    && stronghold.HistoryFinishGroupInfos is not null
                    && stronghold.GroupInfos is not null
                    && stronghold.TeamInfos is not null
                    && stronghold.GroupStageDatas is not null
                    && stronghold.RuneList is not null
                    && stronghold.RewardIds is not null
                    && stronghold.LastResultRecord is not null
                    && stronghold.MineRecords is not null
                    && stronghold.StayDays is not null,
                    "challenge login Stronghold manager fields");
                AssertMailNamedMapKeys(stronghold,
                    ["Id", "BeginTime", "FightBeginTime", "CurDay", "AssistCharacterId",
                        "SetAssistCharacterTime", "BorrowCount", "ElectricEnergy", "Endurance",
                        "MineralLeft", "TotalMineral", "ElectricCharacterIds", "FinishGroupIds",
                        "FinishGroupInfos", "HistoryFinishGroupInfos", "GroupInfos", "TeamInfos",
                        "GroupStageDatas", "RuneList", "RewardIds", "LastResultRecord", "MineRecords",
                        "LevelId", "StayDays"], "challenge login Stronghold wire fields");
            }
            if (!required.Contains(push.Name, StringComparer.Ordinal))
                continue;
            observed.Add(push.Name);
            positions.TryAdd(push.Name, index);
            switch (push.Name)
            {
                case nameof(NotifyRepeatChallengeData):
                {
                    NotifyRepeatChallengeData repeat = MessagePackSerializer.Deserialize<NotifyRepeatChallengeData>(push.Content);
                    AssertEqual(true, repeat.ExpInfo is not null && repeat.RcChapters is not null && repeat.RewardIds is not null,
                        "challenge login Repeat backing data");
                    break;
                }
                case nameof(NotifyArenaActivity):
                {
                    NotifyArenaActivity arena = MessagePackSerializer.Deserialize<NotifyArenaActivity>(push.Content);
                    AssertEqual(true, arena.MaxPointStageList is not null, "challenge login Arena backing data");
                    break;
                }
                case nameof(NotifyFubenBossSingleData):
                {
                    NotifyFubenBossSingleData boss = MessagePackSerializer.Deserialize<NotifyFubenBossSingleData>(push.Content);
                    AssertEqual(true, boss.FubenBossSingleData is not null,
                        "challenge login Boss backing data");
                    break;
                }
                case nameof(NotifyTransfiniteData):
                {
                    NotifyTransfiniteData transfinite = MessagePackSerializer.Deserialize<NotifyTransfiniteData>(push.Content);
                    AssertEqual(true, transfinite.TransfiniteData is not null, "challenge login Transfinite backing data");
                    break;
                }
            }
        }
        foreach (string name in required)
            AssertEqual(true, observed.Contains(name), $"challenge login {name} push");
        AssertEqual(true, positions[nameof(NotifyArenaActivity)] < positions[nameof(NotifyFubenBossSingleData)]
            && positions[nameof(NotifyFubenBossSingleData)] < positions[nameof(NotifyRepeatChallengeData)]
            && positions[nameof(NotifyRepeatChallengeData)] < positions[nameof(NotifyStrongholdLoginData)]
            && positions[nameof(NotifyStrongholdLoginData)] < positions[nameof(NotifyTransfiniteData)],
            "challenge login retail order Arena, Boss, Repeat, Stronghold, Transfinite");
        AssertEqual(true, strongholdSeen, "challenge login emits typed Stronghold integration");
        BsonDocument legacyDocument = loginPlayer.ToBsonDocument();
        BsonDocument legacyStronghold = legacyDocument["stronghold"].AsBsonDocument;
        foreach (string field in legacyStronghold.Names.ToArray())
            legacyStronghold.Remove(field);
        legacyStronghold["electric_character_ids"] = BsonNull.Value;
        legacyStronghold["last_result_record"] = BsonNull.Value;
        Player legacyPlayer = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<Player>(legacyDocument);
        AssertEqual(null, legacyPlayer.Stronghold.ElectricCharacterIds, "legacy BSON preserves null electric character ids");
        AssertEqual(null, legacyPlayer.Stronghold.LastResultRecord, "legacy BSON preserves null last result record");
        using LoopbackSessionHarness legacyHarness = new(
            CreateDrawCompatibilityCharacter(uid + 3),
            legacyPlayer,
            CreateDrawCompatibilityInventory(uid + 3, []),
            "challenge-login-legacy-stronghold");
        legacyHarness.Session.stage = CreateLoginAccountCompatibilityStage(uid + 3);
        try
        {
            doLogin.Invoke(null, [legacyHarness.Session, false]);
        }
        catch (Exception exception)
        {
            Exception cause = exception is System.Reflection.TargetInvocationException { InnerException: Exception inner }
                ? inner
                : exception;
            throw new InvalidDataException($"legacy Stronghold login threw {cause.GetType().FullName}: {cause.Message}", cause);
        }
        List<string> legacyPackets = [];
        while (legacyHarness.TryReadAvailablePacket("legacy Stronghold startup packet", out Packet legacyPacket))
        {
            if (legacyPacket.Type != Packet.ContentType.Push)
                throw new InvalidDataException($"legacy Stronghold login emitted {legacyPacket.Type} packet.");
            Packet.Push legacyPush = MessagePackSerializer.Deserialize<Packet.Push>(legacyPacket.Content);
            legacyPackets.Add(legacyPush.Name);
            if (legacyPush.Name == nameof(NotifyStrongholdLoginData))
            {
                NotifyStrongholdLoginData legacyPayload =
                    MessagePackSerializer.Deserialize<NotifyStrongholdLoginData>(legacyPush.Content);
                AssertEqual(true, legacyPayload.Id > 0 && legacyPayload.LevelId > 0
                    && legacyPayload.BeginTime > 0 && legacyPayload.FightBeginTime > 0,
                    "legacy login Stronghold activation");
                AssertEqual(true, legacyPayload.ElectricCharacterIds is not null
                    && legacyPayload.LastResultRecord is not null,
                    "legacy login Stronghold null state is repaired before wire");
            }
        }
        AssertEqual(true, legacyPackets.Contains(nameof(NotifyTransfiniteData)), "legacy login keeps Transfinite visible");
        AssertEqual(true, legacyPackets.Contains(nameof(NotifyStrongholdLoginData)),
            "legacy login repairs and emits Stronghold integration");

        int claimedOnlyRewardId = row10131.reward.Id;
        int rewardOnlyRewardId = exactEnergy.reward.Id;
        int[] expectedLegacyRewardIds = [claimedOnlyRewardId, rewardOnlyRewardId];
        expectedLegacyRewardIds = expectedLegacyRewardIds.Distinct().OrderBy(id => id).ToArray();
        player.Stronghold.RewardIds = [];
        player.Stronghold.ClaimedRewardIds = [rewardOnlyRewardId, claimedOnlyRewardId, rewardOnlyRewardId];
        RequiredMethod(module, "PrepareLogin", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic,
            [typeof(Player)]).Invoke(null, [player]);
        NotifyStrongholdLoginData claimedOnlyLogin = (NotifyStrongholdLoginData)RequiredMethod(
            module, "BuildLoginData", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic,
            [typeof(Player)]).Invoke(null, [player])!;
        AssertEqual(true, claimedOnlyLogin.RewardIds.SequenceEqual(expectedLegacyRewardIds),
            "claimed-only legacy rewards project on login");
        AssertEqual(true, player.Stronghold.RewardIds.SequenceEqual(player.Stronghold.ClaimedRewardIds),
            "claimed-only legacy rewards normalize both persisted lists");
        AssertEqual(false, ReferenceEquals(player.Stronghold.RewardIds, player.Stronghold.ClaimedRewardIds),
            "Stronghold canonical reward lists do not alias");
        Player reloadedClaimedOnly = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<Player>(player.ToBson());
        AssertEqual(true, reloadedClaimedOnly.Stronghold.RewardIds.SequenceEqual(expectedLegacyRewardIds)
            && reloadedClaimedOnly.Stronghold.ClaimedRewardIds.SequenceEqual(expectedLegacyRewardIds),
            "claimed-only legacy rewards BSON round-trip preserves canonical lists");
        byte[] normalizedState = player.ToBson();
        _ = RequiredMethod(module, "BuildLoginData", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic,
            [typeof(Player)]).Invoke(null, [player]);
        AssertEqual(Convert.ToHexString(normalizedState), Convert.ToHexString(player.ToBson()),
            "Stronghold reward normalization is idempotent");

        byte[] beforeClaimedOnlyRetry = player.ToBson();
        int claimedOnlyClaimCount = h.Session.inventory.AppliedRewardClaims.Count(key =>
            key == $"stronghold:{uid}:achievement:{claimedOnlyRewardId}");
        int claimedOnlyRetryPacketId = rewardPacketId++;
        InvokeRegisteredRequestHandler(nameof(GetStrongholdRewardRequest), h.Session, claimedOnlyRetryPacketId,
            new GetStrongholdRewardRequest { Ids = [claimedOnlyRewardId] });
        AssertEqual(20113018, ReadResponsePayload<GetStrongholdRewardResponse>(
            h, claimedOnlyRetryPacketId, nameof(GetStrongholdRewardResponse), "Stronghold claimed-only duplicate reward claim").Code,
            "claimed-only legacy reward retry uses retail rejection code");
        AssertEqual(claimedOnlyClaimCount, h.Session.inventory.AppliedRewardClaims.Count(key =>
            key == $"stronghold:{uid}:achievement:{claimedOnlyRewardId}"),
            "claimed-only legacy reward retry does not grant twice");
        AssertEqual(Convert.ToHexString(beforeClaimedOnlyRetry), Convert.ToHexString(player.ToBson()),
            "claimed-only legacy reward retry does not mutate state");

        player.Stronghold.RewardIds = [rewardOnlyRewardId];
        player.Stronghold.ClaimedRewardIds = [];
        RequiredMethod(module, "PrepareLogin", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic,
            [typeof(Player)]).Invoke(null, [player]);
        AssertEqual(true, player.Stronghold.RewardIds.SequenceEqual([rewardOnlyRewardId])
            && player.Stronghold.ClaimedRewardIds.SequenceEqual([rewardOnlyRewardId]),
            "reward-only legacy state migrates both persisted lists");
        Player reloadedRewardOnly = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<Player>(player.ToBson());
        AssertEqual(true, reloadedRewardOnly.Stronghold.RewardIds.SequenceEqual([rewardOnlyRewardId])
            && reloadedRewardOnly.Stronghold.ClaimedRewardIds.SequenceEqual([rewardOnlyRewardId]),
            "reward-only legacy state BSON round-trip preserves canonical lists");

        Console.WriteLine("Stronghold compatibility: loopback endpoints, exact responses, boundaries, login, continuation, rewards, and relogin passed.");
    }
}
