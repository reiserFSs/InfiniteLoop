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
        using MongoCollectionOverride stageMongo = MongoCollectionOverride.InstallForStudyProgressionCompatibility(out _);
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
        player.Stronghold.BeginTime = checked((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
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
        Packet firstSettlePacket = h.ReadPacket("Stronghold normal settle");
        List<string> settlePushes = [];
        while (firstSettlePacket.Type == Packet.ContentType.Push)
        {
            settlePushes.Add(MessagePackSerializer.Deserialize<Packet.Push>(firstSettlePacket.Content).Name);
            firstSettlePacket = h.ReadPacket("Stronghold normal settle");
        }
        FightSettleResponse firstSettle = ReadResponsePayload<FightSettleResponse>(firstSettlePacket, nameof(FightSettleResponse));
        AssertEqual(true, settlePushes.Contains(nameof(NotifyUpdateStrongholdGroupData))
            && settlePushes.Contains(nameof(NotifyStrongholdEnduranceData)), "normal settle publishes durable group and endurance");
        AssertEqual(0, firstSettle.Code, "Stronghold normal settle code");
        AssertEqual(true, player.Stronghold.GroupInfos.Single(value => value.Id == groupId).FinishStageIds.Contains((int)firstStage),
            "Stronghold normal settle persists the exact stage clear");
        AssertEqual(true, player.Stronghold.PendingStageId > 0,
            "Stronghold normal settle preserves the next selectable stage");

        StrongholdGroupTable sweepGroup = groupRows.Single(row => row.Id == groupId);
        int sweepConditionId = sweepGroup.SweepCondition[sweepGroup.SweepLevelId.IndexOf(player.Stronghold.LevelId)];
        ConditionTable sweepCondition = TableReaderV2.Parse<ConditionTable>().Single(row => row.Id == sweepConditionId);
        player.Stronghold.HistoryFinishGroupInfos.Add(new()
        {
            Id = sweepCondition.Params[0],
            UsedSystemElectricEnergy = sweepCondition.Params[1]
        });
        int sweepPacketId = 48_829;
        while (!player.Stronghold.FinishGroupIds.Contains(groupId))
        {
            InvokeRegisteredRequestHandler(nameof(SweepStrongholdStageRequest), h.Session, sweepPacketId++,
                new SweepStrongholdStageRequest { GroupId = groupId });
            SweepStrongholdStageResponse sweep = ReadStrongholdResponse<SweepStrongholdStageResponse>(h, sweepPacketId - 1, out _);
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
        int rewardPacketId = 48_840;
        (StrongholdRewardTable reward, ConditionTable condition) row10131 =
            claimRows.Single(pair => pair.condition.Type == 10131);
        List<(StrongholdRewardTable reward, ConditionTable condition)> energyRows =
            claimRows.Where(pair => pair.condition.Type == 12103 && pair.condition.Params.Count >= 3
                && (pair.condition.Params.Count < 4 || pair.condition.Params[3] == 0))
                .GroupBy(pair => pair.condition.Params[0]).Select(group => group.First()).Take(2).ToList();
        if (energyRows.Count < 2)
            throw new InvalidDataException("Stronghold reward fixture requires current-energy conditions for two distinct groups.");
        (StrongholdRewardTable reward, ConditionTable condition) missingEnergy = energyRows[0];
        (StrongholdRewardTable reward, ConditionTable condition) exactEnergy = energyRows[1];
        claimRows = [row10131, missingEnergy, exactEnergy];
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
        GetStrongholdRewardResponse missingEnergyResponse = ReadStrongholdResponse<GetStrongholdRewardResponse>(
            h, rewardPacketId++, out var rewardPushes);
        AssertEqual(true, rewardPushes.Contains(nameof(NotifyItemDataList)), "Stronghold achievement rewards update client inventory");
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
        GetStrongholdRewardResponse eligibleBatch = ReadStrongholdResponse<GetStrongholdRewardResponse>(
            h, rewardPacketId++, out _);
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

    private static TResponse ReadStrongholdResponse<TResponse>(
        LoopbackSessionHarness harness, int packetId, out List<string> pushes)
    {
        pushes = [];
        while (true)
        {
            Packet packet = harness.ReadPacket("Stronghold response packet");
            if (packet.Type == Packet.ContentType.Push)
            {
                Packet.Push push = MessagePackSerializer.Deserialize<Packet.Push>(packet.Content);
                pushes.Add(push.Name);
                continue;
            }
            Packet.Response response = MessagePackSerializer.Deserialize<Packet.Response>(packet.Content);
            AssertEqual(packetId, response.Id, "Stronghold response correlates request id");
            return ReadResponsePayload<TResponse>(packet, typeof(TResponse).Name);
        }
    }

    private static void ValidateStrongholdSweepCompatibility()
    {
        PacketFactory.LoadPacketHandlers();
        using MongoCollectionOverride stageMongo = MongoCollectionOverride.InstallForStudyProgressionCompatibility(out var stageSaves);
        const long uid = 48_901;
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
            out var playerSaves, out _, out var inventorySaves);
        Player player = CreateDrawCompatibilityPlayer(uid);
        player.PlayerData.Level = 80;
        using LoopbackSessionHarness h = new(CreateDrawCompatibilityCharacter(uid), player,
            CreateDrawCompatibilityInventory(uid, []), "stronghold-sweep-loopback");
        h.Session.stage = CreateLoginAccountCompatibilityStage(uid);
        Type module = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.StrongholdModule");
        var prepare = RequiredMethod(module, "PrepareLogin", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic,
            [typeof(Player)]);
        prepare.Invoke(null, [player]);
        var groups = TableReaderV2.Parse<StrongholdGroupTable>().ToDictionary(row => row.Id);
        var conditions = TableReaderV2.Parse<ConditionTable>().ToDictionary(row => row.Id);
        var selected = player.Stronghold.GroupStageDatas
            .Where(data => groups[data.Id].PreId is not > 0 && groups[data.Id].FinishRelatedId.All(id => id <= 0)
                && groups[data.Id].SweepLevelId.Contains(player.Stronghold.LevelId) && data.StageIds.Count > 1)
            .Take(2).ToArray();
        AssertEqual(2, selected.Length, "Stronghold sweep uses two authoritative groups");
        int packetId = 48_910;

        void Reject(int groupId, int code, string name)
        {
            string beforePlayer = Convert.ToHexString(player.ToBson());
            string beforeInventory = Convert.ToHexString(h.Session.inventory.ToBson());
            InvokeRegisteredRequestHandler(nameof(SweepStrongholdStageRequest), h.Session, packetId,
                new SweepStrongholdStageRequest { GroupId = groupId });
            var response = ReadStrongholdResponse<SweepStrongholdStageResponse>(h, packetId++, out var pushes);
            AssertEqual(code, response.Code, name);
            AssertEqual(0, pushes.Count, $"{name} emits no mutation pushes");
            AssertEqual(beforePlayer, Convert.ToHexString(player.ToBson()), $"{name} leaves player unchanged");
            AssertEqual(beforeInventory, Convert.ToHexString(h.Session.inventory.ToBson()), $"{name} leaves inventory unchanged");
        }

        Reject(-1, 20113018, "unknown group");
        Reject((int)selected[0].StageIds[0], 20113018, "stage id cannot substitute for group id");
        Reject(selected[0].Id, 20113071, "missing historical clear");
        uint begin = player.Stronghold.BeginTime;
        player.Stronghold.BeginTime = 1;
        Reject(selected[0].Id, 20113001, "expired activity");
        player.Stronghold.BeginTime = begin;

        for (int index = 0; index < selected.Length; index++)
        {
            StrongholdGroupStageData allocation = selected[index];
            StrongholdGroupTable group = groups[allocation.Id];
            ConditionTable condition = conditions[group.SweepCondition[group.SweepLevelId.IndexOf(player.Stronghold.LevelId)]];
            player.Stronghold.HistoryFinishGroupInfos.RemoveAll(info => info.Id == condition.Params[0]);
            StrongholdFinishGroupInfo history = new() { Id = condition.Params[0] };
            if (condition.Params[2] != 0) history.UsedSystemElectricEnergy = condition.Params[1] + 1;
            else history.UsedElectricEnergy = condition.Params[1] + 1;
            player.Stronghold.HistoryFinishGroupInfos.Add(history);
            Reject(group.Id, 20113071, "historical electricity exceeds threshold");
            history = new() { Id = condition.Params[0] };
            player.Stronghold.HistoryFinishGroupInfos.Add(history);
            if (condition.Params[2] != 0) history.UsedSystemElectricEnergy = condition.Params[1];
            else history.UsedElectricEnergy = condition.Params[1];
            player.Stronghold.Endurance = 0;
            Reject(group.Id, 20113020, "uncleared group needs endurance");
            StrongholdGroupInfo progress = player.Stronghold.GroupInfos.Single(info => info.Id == group.Id);
            if (index == 0)
                progress.FinishStageIds.Add((int)allocation.StageIds[0]);
            else
                player.Stronghold.Endurance = group.Endurance
                    ?? throw new InvalidDataException($"Stronghold fixture group {group.Id} has no endurance cost.");
            int remainingStages = allocation.StageIds.Count - progress.FinishStageIds.Count;
            int beforeCount = player.Stronghold.LastResultRecord.FinishCount;
            int rewardId = group.RewardId[player.Stronghold.LevelId - 1];
            var reward = TableReaderV2.Parse<AscNet.Table.V2.share.reward.RewardTable>().Single(row => row.Id == rewardId);
            var expected = TableReaderV2.Parse<AscNet.Table.V2.share.reward.RewardGoodsTable>()
                .Where(row => reward.SubIds.Contains(row.Id)).ToArray();
            var itemBefore = expected.GroupBy(row => row.TemplateId).ToDictionary(rows => rows.Key,
                rows => h.Session.inventory.Items.FirstOrDefault(item => item.Id == rows.Key)?.Count ?? 0);
            InvokeRegisteredRequestHandler(nameof(SweepStrongholdStageRequest), h.Session, packetId,
                new SweepStrongholdStageRequest { GroupId = group.Id });
            var response = ReadStrongholdResponse<SweepStrongholdStageResponse>(h, packetId++, out var pushes);
            AssertEqual(0, response.Code, $"valid group {group.Id} quick clear");
            AssertEqual(true, response.StrongholdFightResult.AllFinished, "quick clear completes group");
            var result = response.StrongholdFightResult.GroupFightResultInfos.Single();
            AssertEqual(group.Id, result.GroupId, "quick clear response carries selected group");
            AssertEqual(true, expected.Select(row => (row.TemplateId, row.Count)).OrderBy(row => row.TemplateId)
                .SequenceEqual(result.RewardGoodsList.Select(row => (row.TemplateId, row.Count)).OrderBy(row => row.TemplateId)),
                "quick clear grants group reward once, not once per stage");
            AssertEqual(beforeCount + remainingStages, player.Stronghold.LastResultRecord.FinishCount,
                "quick clear counts only newly completed stages");
            AssertEqual(0, player.Stronghold.Endurance, "quick clear consumes only unpaid group endurance");
            AssertEqual(true, allocation.StageIds.All(id => progress.FinishStageIds.Contains((int)id)),
                "quick clear completes allocated stages");
            AssertEqual(true, pushes.IndexOf(nameof(NotifyStrongholdFinishGroupId)) >= 0
                && pushes.IndexOf(nameof(NotifyStrongholdFinishGroupId)) < pushes.IndexOf(nameof(NotifyDeleteStrongholdGroupData))
                && pushes.Last() == nameof(NotifyStrongholdEnduranceData), "quick clear publishes completion before response");
            Player persisted = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<Player>(playerSaves.LastReplacement!.ToBson());
            Inventory persistedInventory = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<Inventory>(inventorySaves.LastReplacement!.ToBson());
            AssertEqual(true, persisted.Stronghold.FinishGroupIds.Contains(group.Id), "quick clear completion is durable");
            AssertEqual(0, persisted.Stronghold.PendingStageId, "quick clear leaves no pending battle");
            Stage persistedStage = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<Stage>(stageSaves.LastReplacement!.ToBson());
            AssertEqual(true, allocation.StageIds.Skip(index == 0 ? 1 : 0).All(id => persistedStage.Stages[id].Passed),
                "quick clear stage progression is durable");
            foreach (var rows in expected.GroupBy(row => row.TemplateId))
                AssertEqual(itemBefore[rows.Key] + rows.Sum(row => (long)row.Count),
                    persistedInventory.Items.Single(item => item.Id == rows.Key).Count, "quick clear inventory is durable");
            h.Session.player = player = persisted;
            h.Session.inventory = persistedInventory;
            Reject(group.Id, 20113070, "reloaded completed group cannot grant twice");
        }

        StrongholdGroupTable relatedGroup = groups.Values.First(group =>
            group.SweepLevelId.Contains(player.Stronghold.LevelId)
            && group.FinishRelatedId.Any(id => id > 0)
            && group.FinishRelatedId.Where(id => id > 0).All(id => !player.Stronghold.FinishGroupIds.Contains(id)));
        int[] relatedIds = relatedGroup.FinishRelatedId.Where(id => id > 0).Append(relatedGroup.Id).ToArray();
        foreach (int id in relatedIds)
            if (groups[id].PreId is int predecessor && predecessor > 0 && !player.Stronghold.FinishGroupIds.Contains(predecessor))
                player.Stronghold.FinishGroupIds.Add(predecessor);
        ConditionTable relatedCondition = conditions[relatedGroup.SweepCondition[relatedGroup.SweepLevelId.IndexOf(player.Stronghold.LevelId)]];
        player.Stronghold.HistoryFinishGroupInfos.Add(new()
        {
            Id = relatedCondition.Params[0],
            UsedSystemElectricEnergy = relatedCondition.Params[1],
            UsedElectricEnergy = relatedCondition.Params[1]
        });
        int relatedCost = relatedIds.Sum(id => groups[id].Endurance
            ?? throw new InvalidDataException($"Stronghold fixture group {id} has no endurance cost."));
        player.Stronghold.Endurance = relatedCost - 1;
        Reject(relatedGroup.Id, 20113020, "related groups require total endurance before any settlement");
        player.Stronghold.Endurance = relatedCost;
        uint originalStageId = player.Stronghold.GroupStageDatas.Single(data => data.Id == relatedGroup.Id).StageIds[0];
        player.Stronghold.GroupStageDatas.Single(data => data.Id == relatedGroup.Id).StageIds[0] = int.MaxValue;
        Reject(relatedGroup.Id, 20113018, "invalid allocated stage rejects entire related group batch");
        player.Stronghold.GroupStageDatas.Single(data => data.Id == relatedGroup.Id).StageIds[0] = originalStageId;
        InvokeRegisteredRequestHandler(nameof(SweepStrongholdStageRequest), h.Session, packetId,
            new SweepStrongholdStageRequest { GroupId = relatedGroup.Id });
        var relatedResponse = ReadStrongholdResponse<SweepStrongholdStageResponse>(h, packetId++, out _);
        AssertEqual(0, relatedResponse.Code, "related-group quick clear succeeds");
        AssertEqual(true, relatedIds.Order().SequenceEqual(relatedResponse.StrongholdFightResult.GroupFightResultInfos
            .Select(info => info.GroupId).Order()), "related-group quick clear returns one result per completed group");
        AssertEqual(true, relatedIds.All(id => player.Stronghold.FinishGroupIds.Contains(id)),
            "related-group quick clear completes every table-related group");
        AssertEqual(0, player.Stronghold.Endurance, "related-group quick clear charges exact total cost");

        StrongholdGroupStageData retained = player.Stronghold.GroupStageDatas.First();
        uint[] retainedStages = retained.StageIds.ToArray();
        int endurance = player.Stronghold.Endurance;
        player.Stronghold.GroupStageDatas.RemoveAll(data => data.Id != retained.Id);
        prepare.Invoke(null, [player]);
        AssertEqual(true, retainedStages.SequenceEqual(player.Stronghold.GroupStageDatas.Single(data => data.Id == retained.Id).StageIds),
            "legacy allocation repair preserves existing stage selection");
        AssertEqual(true, selected.All(data => player.Stronghold.GroupStageDatas.Any(restored => restored.Id == data.Id)),
            "legacy allocation repair restores eligible groups");
        AssertEqual(endurance, player.Stronghold.Endurance, "legacy allocation repair does not refill endurance");
        Console.WriteLine("Stronghold sweep compatibility: group IDs, historical gates, endurance, socket results, rewards, repair and durable retry passed.");
    }
}
