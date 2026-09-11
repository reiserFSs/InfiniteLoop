using AscNet.Common;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Game;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.character;
using AscNet.Table.V2.share.character.grade;
using AscNet.Table.V2.share.character.quality;
using AscNet.Table.V2.share.fuben;
using AscNet.Table.V2.share.fuben.transfinite;
using AscNet.Table.V2.share.fuben.arena;
using AscNet.Table.V2.share.fuben.bosssingle;
using AscNet.Table.V2.share.item;
using AscNet.Table.V2.share.reward;
using AscNet.Table.V2.share.task;
using AscNet.Table.V2.share.wheelchairmanual;
using MessagePack;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Newtonsoft.Json.Linq;
using System.Reflection;
using LoginTask = AscNet.Common.MsgPack.NotifyTaskData.NotifyTaskDataTaskData.NotifyTaskDataTaskDataTask;
using AscNet.Table.V2.share.character.skill;
using AscNet.Table.V2.share.equip;

namespace AscNet.Test;

internal static partial class Program
{
    private static void ValidateWheelchairManualNaturalTaskCompatibility()
    {
        PacketFactory.LoadPacketHandlers();
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForStoryDeployVersionGapCompatibility(out var savedPlayers);
        var activity = TableReaderV2.Parse<WheelchairManualActivityTable>().Single();
        var plans = TableReaderV2.Parse<WheelchairManualBattlePassPlanTable>().ToDictionary(row => row.Id);
        var plan = plans[activity.PlanIds.Min()];
        var tasks = TableReaderV2.Parse<CurrentTaskTable>().ToDictionary(row => row.Id);
        var conditions = TableReaderV2.Parse<ConditionTable>().ToDictionary(row => row.Id);
        var stages = TableReaderV2.Parse<StageTable>().ToDictionary(row => row.StageId);
        var rewards = TableReaderV2.Parse<RewardTable>().ToDictionary(row => row.Id);
        var goods = TableReaderV2.Parse<RewardGoodsTable>();
        var items = TableReaderV2.Parse<ItemTable>();
        string Signature(IEnumerable<RewardGoods> values) => string.Join(";", values.OrderBy(row => row.TemplateId)
            .Select(row => $"{row.TemplateId}:{row.Count}"));
        string Expected(int rewardId) => Signature(goods.Where(row => rewards[rewardId].SubIds.Contains(row.Id))
            .Select(row => new RewardGoods { TemplateId = row.TemplateId, Count = row.Count }));
        int packetId = 49_800;

        for (int variant = 0; variant < 2; variant++)
        {
            long uid = 49_800 + variant;
            Player player = CreateDrawCompatibilityPlayer(uid);
            player.MissionProgress = new();
            player.PlayerData.Level = checked((uint)(conditions[tasks[8007].Condition].Params[0] + variant));
            Character roster = CreateDrawCompatibilityCharacter(uid);
            var characterRow = TableReaderV2.Parse<CharacterTable>().Where(row => row.Type == 1 && row.EquipId > 0 && Character.IsOwnableCharacter((uint)row.Id))
                .OrderBy(row => row.Id).Skip(variant).First();
            var owned = roster.AddCharacter((uint)characterRow.Id);
            var equip = owned.Equip ?? throw new InvalidDataException("Natural manual fixture requires starting equipment.");
            int characterTarget = conditions[tasks[8005].Condition].Params[2];
            int characterExp = Character.characterLevelUpTemplates.Where(row => row.Type == characterRow.LevelUpTemplateId
                && row.Level >= owned.Character.Level && row.Level < characterTarget).Sum(row => row.Exp);
            var characterFood = items.Where(row => row.GetCharacterExp(characterRow.Type) > 0)
                .OrderBy(row => row.GetCharacterExp(characterRow.Type)).First();
            int characterFoodCount = (characterExp + characterFood.GetCharacterExp(characterRow.Type) - 1)
                / characterFood.GetCharacterExp(characterRow.Type);
            int equipTarget = conditions[tasks[8006].Condition].Params[6];
            var breakthrough = Character.ResolveEquipBreakThrough(equip.TemplateId, equip.Breakthrough)!;
            int equipExp = Character.equipLevelUpTemplates.Where(row => row.TemplateId == breakthrough.LevelUpTemplateId
                && row.Level >= equip.Level && row.Level < equipTarget).Sum(row => row.Exp);
            var equipFood = items.Where(row => row.GetEquipUpgradeInfo().Exp > 0 && row.SubTypeParams.FirstOrDefault() == 1)
                .OrderBy(row => row.GetEquipUpgradeInfo().Exp).First();
            int equipFoodCount = (equipExp + equipFood.GetEquipUpgradeInfo().Exp - 1) / equipFood.GetEquipUpgradeInfo().Exp;
            Inventory inventory = CreateDrawCompatibilityInventory(uid,
            [
                new Item { Id = characterFood.Id, Count = characterFoodCount },
                new Item { Id = equipFood.Id, Count = equipFoodCount },
                new Item { Id = Inventory.Coin, Count = equipFood.GetEquipUpgradeInfo().Cost * equipFoodCount },
                new Item { Id = Inventory.ActionPoint, Count = 10_000 }
            ]);
            // Persist synthetic account setup, never task completion or reward claims.
            player = BsonSerializer.Deserialize<Player>(player.ToBson());
            roster = BsonSerializer.Deserialize<Character>(roster.ToBson());
            inventory = BsonSerializer.Deserialize<Inventory>(inventory.ToBson());
            using LoopbackSessionHarness harness = new(roster, player, inventory, $"manual-natural-{variant}");
            harness.Session.stage = CreateLoginAccountCompatibilityStage(uid);
            LoginTask Task(int id) => BuildTaskData(harness.Session).Single(row => row.Id == id);
            T Request<T>(string name, object? request, out NotifyWheelchairManualActivity? manual)
            {
                int id = packetId++;
                manual = null;
                InvokeRegisteredRequestHandler(name, harness.Session, id, request);
                for (int index = 0; index < 64; index++)
                {
                    Packet packet = harness.ReadPacket(name);
                    if (packet.Type == Packet.ContentType.Push)
                    {
                        var push = MessagePackSerializer.Deserialize<Packet.Push>(packet.Content);
                        if (push.Name == nameof(NotifyWheelchairManualActivity))
                            manual = MessagePackSerializer.Deserialize<NotifyWheelchairManualActivity>(push.Content);
                        continue;
                    }
                    AssertEqual(Packet.ContentType.Response, packet.Type, $"{name} response type");
                    var response = MessagePackSerializer.Deserialize<Packet.Response>(packet.Content);
                    AssertEqual(id, response.Id, $"{name} correlation");
                    AssertEqual(typeof(T).Name, response.Name, $"{name} response name");
                    AssertNoAvailablePacket(harness, $"{name} updates precede callback");
                    return MessagePackSerializer.Deserialize<T>(response.Content);
                }
                throw new InvalidDataException($"{name} response missing.");
            }
            WheelchairManualGetPlanRewardResponse ClaimPlan(out NotifyWheelchairManualActivity? push) =>
                Request<WheelchairManualGetPlanRewardResponse>("WheelchairManualGetPlanRewardRequest", null, out push);
            AssertEqual(true, ClaimPlan(out _).Code != 0, "Natural incomplete first plan rejects");
            AssertEqual(false, Task(8005).State == 3, "Character task begins incomplete");
            AssertEqual(false, Task(8006).State == 3, "Equipment task begins incomplete");
            AssertEqual(0, Request<CharacterLevelUpResponse>(nameof(CharacterLevelUpRequest), new CharacterLevelUpRequest
            {
                TemplateId = (uint)characterRow.Id, UseItems = new() { [characterFood.Id] = characterFoodCount }
            }, out _).Code, "Natural character enhancement succeeds");
            AssertEqual(0, Request<EquipLevelUpResponse>(nameof(EquipLevelUpRequest), new EquipLevelUpRequest
            {
                EquipId = checked((int)equip.Id), UseItems = new() { [equipFood.Id] = equipFoodCount }
            }, out _).Code, "Natural equipment enhancement succeeds");

            void Clear(int stageId)
            {
                if (harness.Session.stage.Stages.TryGetValue(stageId, out var passed) && passed.Passed) return;
                foreach (int predecessor in stages[stageId].PreStageId.Where(id => id > 0)) Clear(predecessor);
                var start = Request<PreFightResponse>(nameof(PreFightRequest), new PreFightRequest
                {
                    PreFightData = new() { StageId = (uint)stageId, ChallengeCount = 1,
                        CardIds = [(uint)characterRow.Id], CaptainPos = 1, FirstFightPos = 1 }
                }, out _);
                AssertEqual(0, start.Code, $"Natural stage {stageId} pre-fight");
                AssertEqual(0, Request<FightSettleResponse>(nameof(FightSettleRequest),
                    CreateMissingStageSettleRequest((uint)stageId, start.FightData.FightId, uid), out _).Code,
                    $"Natural stage {stageId} settle");
                AssertEqual(true, harness.Session.stage.Stages[stageId].Passed, "Real settlement persists clear");
            }
            foreach (int taskId in plan.TaskIds.Where(id => conditions[tasks[id].Condition].Type is 15101 or 15225))
                Clear(conditions[tasks[taskId].Condition].Params[0]);

            int matchingGroup = conditions[tasks[8009].Condition].Params[1];
            Type drawManager = RequiredAscNetGameServerType("AscNet.GameServer.Game.DrawManager");
            List<DrawInfo> Draws(int groupId) => (List<DrawInfo>)RequiredMethod(drawManager,
                "GetDrawInfosByGroup", BindingFlags.Public | BindingFlags.Static, [typeof(int), typeof(Player)])
                .Invoke(null, [groupId, player])!;
            var matching = Draws(matchingGroup).First();
            var groups = (List<DrawGroupInfo>)RequiredMethod(drawManager, "GetDrawGroupInfos",
                BindingFlags.Public | BindingFlags.Static, [typeof(Player)]).Invoke(null, [player])!;
            var other = groups.Where(row => row.Id != matchingGroup).SelectMany(row => Draws(row.Id)).First();
            DrawDrawCardResponse Draw(DrawInfo draw) => Request<DrawDrawCardResponse>(nameof(DrawDrawCardRequest),
                new DrawDrawCardRequest { DrawId = draw.Id, Count = 1, UseDrawTicketId = 0 }, out _);
            inventory.Items.RemoveAll(row => row.Id == matching.UseItemId);
            AssertEqual(true, Draw(matching).Code != 0, "Unfunded Basic Research rejects");
            AssertEqual(false, Task(8009).State == 3, "Rejected draw cannot complete manual task");
            inventory.Do(other.UseItemId, other.UseItemCount);
            AssertEqual(0, Draw(other).Code, "Nonmatching research succeeds");
            AssertEqual(false, Task(8009).State == 3, "Different research group cannot complete Basic Research task");
            inventory.Do(matching.UseItemId, matching.UseItemCount);
            AssertEqual(0, Draw(matching).Code, "Basic Research succeeds");
            AssertEqual(3, Task(8009).State, "Actual matching draw completes manual task");
            harness.Session.player = player = BsonSerializer.Deserialize<Player>(savedPlayers.LastReplacement!.ToBson());
            AssertEqual(3, Task(8009).State, "Basic Research completion survives persisted BSON reload");
            AssertEqual(true, ClaimPlan(out _).Code != 0, "Achieved but unclaimed tasks cannot unlock plan reward");
            foreach (int taskId in plan.TaskIds)
            {
                AssertEqual(3, Task(taskId).State, $"Natural task {taskId} achieved without completion seeding");
                var response = Request<FinishTaskResponse>(nameof(FinishTaskRequest), new FinishTaskRequest { TaskId = taskId }, out _);
                AssertEqual(0, response.Code, $"Natural task {taskId} reward claim");
                AssertEqual(Expected(tasks[taskId].RewardId), Signature(response.RewardGoodsList), $"Task {taskId} authoritative rewards");
                AssertEqual(4, Task(taskId).State, $"Natural task {taskId} claimed");
            }
            var claimed = ClaimPlan(out var update);
            AssertEqual(0, claimed.Code, "Naturally completed first plan claim");
            AssertEqual(Expected(plan.RewardId), Signature(claimed.RewardList), "Natural plan authoritative reward");
            AssertEqual(true, update?.GetRewardPlanIds.Contains(plan.Id) == true, "Plan receipt arrives before response");
            harness.Session.player = player = BsonSerializer.Deserialize<Player>(savedPlayers.LastReplacement!.ToBson());
            AssertEqual(true, player.WheelchairManualClaimedPlanIds[activity.Id].Contains(plan.Id), "Natural plan receipt persists");
            string beforeInventory = inventory.ToJson();
            string beforeRoster = roster.ToJson();
            AssertEqual(true, ClaimPlan(out _).Code != 0, "Relogged duplicate plan claim rejects");
            AssertEqual(true, Request<FinishTaskResponse>(nameof(FinishTaskRequest), new FinishTaskRequest { TaskId = 8009 }, out _).Code != 0,
                "Relogged duplicate draw-task reward rejects");
            AssertEqual(beforeInventory, inventory.ToJson(), "Duplicate claims do not duplicate inventory");
            AssertEqual(beforeRoster, roster.ToJson(), "Duplicate claims do not duplicate characters");
        }
        ValidateWheelchairManualTransfiniteTaskCompatibility();
        ValidateWheelchairManualParticipationTaskCompatibility();
        ValidateWheelchairManualPowerTaskCompatibility();
    }

    private static void ValidateWheelchairManualTransfiniteTaskCompatibility()
    {
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForStoryDeployVersionGapCompatibility(out var saved);
        var task = TableReaderV2.Parse<CurrentTaskTable>().Single(row => row.Id == 8042);
        int required = TableReaderV2.Parse<ConditionTable>().Single(row => row.Id == task.Condition).Params[0];
        var stageRows = TableReaderV2.Parse<TransfiniteStageTable>().ToDictionary(row => row.StageId);
        var groups = TableReaderV2.Parse<TransfiniteStageGroupTable>();
        var rotations = TableReaderV2.Parse<TransfiniteRotateGroupTable>();
        var scoreRewards = TableReaderV2.Parse<TransfiniteScoreRewardGroupTable>();
        var linked = (from candidateRegion in TableReaderV2.Parse<TransfiniteRegionTable>()
                      join candidateRotation in rotations on candidateRegion.RotateGroupId equals candidateRotation.RotateGroupId
                      from groupId in candidateRotation.StageGroupId
                      join candidateGroup in groups on groupId equals candidateGroup.StageGroupId
                      where candidateGroup.Type == 2 && candidateGroup.StageId.Count > required
                          && candidateGroup.StageId.All(id => stageRows.TryGetValue(id, out var stage)
                              && (stage.Score ?? 0) >= 0 && (stage.ExtraScore ?? 0) >= 0
                              && (stage.ExtraScore is not > 0 || stage.ExtraTimeLimit is > 0))
                          && scoreRewards.Any(row => row.RegionId == candidateRegion.RegionId
                              && row.ScoreRewardGroupId == candidateRegion.ScoreRewardGroupId)
                      select (Region: candidateRegion, Rotation: candidateRotation, Group: candidateGroup)).First();
        var (region, rotation, group) = linked;
        const long uid = 49_850;
        Player player = CreateDrawCompatibilityPlayer(uid);
        player.MissionProgress = new();
        player.Transfinite = new()
        {
            ActivityId = TableReaderV2.Parse<TransfiniteActivityTable>().First().Id,
            ActivityAuthorizedUntil = long.MaxValue, CircleId = 1, RegionId = region.RegionId,
            ScoreRewardGroupId = region.ScoreRewardGroupId, StageGroupId = group.StageGroupId,
            StageGroupIndex = rotation.StageGroupId.IndexOf(group.StageGroupId)
        };
        Character roster = CreateDrawCompatibilityCharacter(uid);
        uint characterId = (uint)TableReaderV2.Parse<CharacterTable>().First(row => row.Type == 1 && row.EquipId > 0 && Character.IsOwnableCharacter((uint)row.Id)).Id;
        roster.AddCharacter(characterId);
        using LoopbackSessionHarness harness = new(roster, player, CreateDrawCompatibilityInventory(uid, []), "manual-transfinite-natural");
        harness.Session.stage = CreateLoginAccountCompatibilityStage(uid);
        int packetId = 50_000;
        T Request<T>(string name, object request)
        {
            int id = packetId++;
            InvokeRegisteredRequestHandler(name, harness.Session, id, request);
            T response = (T)ReadResponsePayload(harness, id, typeof(T).Name, name, typeof(T), maxPacketsToRead: 64);
            AssertNoAvailablePacket(harness, $"{name} pushes precede callback");
            return response;
        }
        int Progress() => player.MissionProgress.ConditionCounters.GetValueOrDefault(task.Condition);
        void Team() => AssertEqual(0, Request<TransfiniteSetTeamResponse>(nameof(TransfiniteSetTeamRequest),
            new TransfiniteSetTeamRequest
            {
                StageGroupId = group.StageGroupId, ResetStageIndex = true,
                TeamInfo = new() { CharacterIdList = [characterId, 0, 0], CaptainPos = 1, FirstFightPos = 1 }
            }).Code, "Manual Transfinite starts real run");
        void Fight(bool win)
        {
            int stageId = group.StageId[player.Transfinite!.BattleInfo!.StageProgressIndex];
            var start = Request<PreFightResponse>(nameof(PreFightRequest), new PreFightRequest
                { PreFightData = new() { StageId = (uint)stageId, CardIds = [], RobotIds = [] } });
            AssertEqual(0, start.Code, "Manual Transfinite real pre-fight");
            var settle = CreateMissingStageSettleRequest((uint)stageId, start.FightData.FightId, uid);
            settle.Result.IsWin = win;
            settle.Result.LeftTime = 1;
            AssertEqual(0, Request<FightSettleResponse>(nameof(FightSettleRequest), settle).Code, "Manual Transfinite real settle");
        }
        TransfiniteConfirmBattleResultResponse Confirm(bool giveUp = false, int? groupId = null) =>
            Request<TransfiniteConfirmBattleResultResponse>(nameof(TransfiniteConfirmBattleResultRequest),
                new TransfiniteConfirmBattleResultRequest { StageGroupId = groupId ?? group.StageGroupId, IsGiveUp = giveUp });

        Team();
        Fight(false);
        AssertEqual(true, Confirm().Code != 0, "Losing Transfinite fight has no confirmable win");
        AssertEqual(0, Progress(), "Loss cannot advance manual streak");
        Fight(true);
        AssertEqual(0, Progress(), "Unconfirmed win cannot advance manual streak");
        AssertEqual(true, Confirm(groupId: int.MaxValue).Code != 0, "Wrong Transfinite group cannot confirm");
        AssertEqual(0, Progress(), "Wrong group cannot advance manual streak");
        AssertEqual(0, Confirm(giveUp: true).Code, "Giving up pending win succeeds");
        AssertEqual(0, Progress(), "Giving up cannot advance manual streak");
        Fight(true);
        AssertEqual(0, Confirm().Code, "First confirmed Transfinite win");
        AssertEqual(1, Progress(), "First confirmation records one stage");
        AssertEqual(true, Confirm().Code != 0, "Duplicate confirmation rejects");
        AssertEqual(1, Progress(), "Duplicate confirmation cannot add progress");
        Team();
        Fight(true);
        AssertEqual(0, Confirm().Code, "Restarted first stage confirms");
        AssertEqual(1, Progress(), "Repeated first stage uses maximum run depth, not additive wins");
        for (int depth = 2; depth <= required; depth++)
        {
            Fight(true);
            AssertEqual(depth - 1, Progress(), "Pending stage never advances manual task");
            AssertEqual(0, Confirm().Code, "Next sequential stage confirms");
            AssertEqual(depth, Progress(), "Confirmed run depth is the manual counter");
        }
        harness.Session.player = player = BsonSerializer.Deserialize<Player>(saved.LastReplacement!.ToBson());
        AssertEqual(required, Progress(), "Confirmed streak persists through BSON reload");
        AssertEqual(3, BuildTaskData(harness.Session).Single(row => row.Id == task.Id).State, "Three confirmed wins achieve manual8042");
        var claim = Request<FinishTaskResponse>(nameof(FinishTaskRequest), new FinishTaskRequest { TaskId = task.Id });
        AssertEqual(0, claim.Code, "Natural Transfinite manual reward claim");
        var reward = TableReaderV2.Parse<RewardTable>().Single(row => row.Id == task.RewardId);
        string Signature(IEnumerable<RewardGoods> values) => string.Join(";", values.OrderBy(row => row.TemplateId)
            .Select(row => $"{row.TemplateId}:{row.Count}"));
        AssertEqual(Signature(TableReaderV2.Parse<RewardGoodsTable>().Where(row => reward.SubIds.Contains(row.Id))
            .Select(row => new RewardGoods { TemplateId = row.TemplateId, Count = row.Count })),
            Signature(claim.RewardGoodsList), "Natural Transfinite manual reward derives from table");
        harness.Session.player = player = BsonSerializer.Deserialize<Player>(saved.LastReplacement!.ToBson());
        string inventoryBefore = harness.Session.inventory.ToJson();
        AssertEqual(4, BuildTaskData(harness.Session).Single(row => row.Id == task.Id).State, "Transfinite task claim survives relog");
        AssertEqual(true, Request<FinishTaskResponse>(nameof(FinishTaskRequest), new FinishTaskRequest { TaskId = task.Id }).Code != 0,
            "Relogged Transfinite task duplicate rejects");
        AssertEqual(inventoryBefore, harness.Session.inventory.ToJson(), "Transfinite task retry cannot duplicate reward");
    }

    private static void ValidateWheelchairManualParticipationTaskCompatibility()
    {
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForBossCompatibility(out var saved, out _);
        var tasks = TableReaderV2.Parse<CurrentTaskTable>().ToDictionary(row => row.Id);
        var conditions = TableReaderV2.Parse<ConditionTable>().ToDictionary(row => row.Id);
        int packetId = 50_200;
        foreach (int taskId in new[] { 8021, 8027 })
        {
            bool arena = taskId == 8021;
            var task = tasks[taskId];
            var condition = conditions[task.Condition];
            uint matchingId = (uint)condition.Params[arena ? 1 : 4];
            uint otherId = (uint)TableReaderV2.Parse<CharacterTable>()
                .First(row => row.Type == 1 && row.Id != matchingId && row.EquipId > 0 && Character.IsOwnableCharacter((uint)row.Id)).Id;
            long uid = 50_200 + taskId;
            Player player = CreateDrawCompatibilityPlayer(uid);
            player.MissionProgress = new();
            player.SimulatedBattlefield = new();
            Character roster = CreateDrawCompatibilityCharacter(uid);
            roster.AddCharacter(matchingId);
            roster.AddCharacter(otherId);
            using LoopbackSessionHarness harness = new(roster, player, CreateDrawCompatibilityInventory(uid, []),
                $"manual-participation-{taskId}");
            harness.Session.stage = CreateLoginAccountCompatibilityStage(uid);
            T Request<T>(string name, object request)
            {
                int id = packetId++;
                InvokeRegisteredRequestHandler(name, harness.Session, id, request);
                T response = (T)ReadResponsePayload(harness, id, typeof(T).Name, name, typeof(T), maxPacketsToRead: 64);
                AssertNoAvailablePacket(harness, $"{name} updates precede response");
                return response;
            }
            int Progress() => player.MissionProgress.ConditionCounters.GetValueOrDefault(task.Condition);
            int areaId = 0;
            int stageId;
            if (arena)
            {
                AssertEqual(0, Request<JoinActivityResponse>("JoinActivityRequest", new Dictionary<string, object>()).Code,
                    "Manual War Zone joins through actual handler");
                var areas = Request<AreaDataResponse>("AreaDataRequest", new Dictionary<string, object>());
                AssertEqual(0, areas.Code, "Manual War Zone area datasource");
                areaId = JArray.FromObject(areas.AreaList!).OfType<JObject>().First().Value<int>("AreaId");
                stageId = InvokePrivateStaticWithArgs<int?>(RequiredAscNetGameServerType("AscNet.GameServer.Handlers.ArenaModule"),
                    "ResolveConfiguredStageId", [TableReaderV2.Parse<AreaStageTable>().Single(row => row.Id == areaId)])!.Value;
            }
            else
            {
                InvokePrivateStaticWithArgs<object?>(RequiredAscNetGameServerType("AscNet.GameServer.Handlers.BossModule"),
                    "PrepareLogin", [harness.Session]);
                int sectionId = player.SimulatedBattlefield.BossList.First();
                stageId = TableReaderV2.Parse<BossSingleSectionTable>()
                    .Single(row => row.SectionId == sectionId && row.AfreshId == 1).StageId[0];
            }
            BossSingleSaveScoreResponse Save() => Request<BossSingleSaveScoreResponse>(nameof(BossSingleSaveScoreRequest),
                new BossSingleSaveScoreRequest { StageId = stageId });
            FightSettleRequest Fight(uint deployed, bool win, bool forceExit = false)
            {
                var start = Request<PreFightResponse>(nameof(PreFightRequest), new PreFightRequest
                {
                    PreFightData = new()
                    {
                        StageId = (uint)stageId, ChallengeCount = 1, CardIds = [deployed], RobotIds = [],
                        FirstFightPos = 1, CaptainPos = 1, SelectAreaId = areaId, ArenaSelectIndex = 0,
                        BossSingleStageType = arena ? 0 : 1
                    }
                });
                AssertEqual(0, start.Code, $"Manual task {taskId} authenticated deployment");
                var settle = CreateMissingStageSettleRequest((uint)stageId, start.FightData.FightId, uid);
                settle.Result.IsWin = win;
                settle.Result.IsForceExit = forceExit;
                settle.Result.StartFrame = 1;
                settle.Result.SettleFrame = 401;
                settle.Result.NpcHpInfo = new()
                {
                    [1] = new NpcHp
                    {
                        CharacterId = (int)deployed, Type = 1, BuffIds = [],
                        AttrTable = new() { [1] = new Dictionary<object, object> { ["Value"] = win ? 100 : 0, ["MaxValue"] = 100 } }
                    },
                    [2] = new NpcHp
                    {
                        Type = 2, BuffIds = [],
                        AttrTable = new() { [1] = new Dictionary<object, object> { ["Value"] = win ? 0 : 100, ["MaxValue"] = 100 } }
                    }
                };
                AssertEqual(0, Request<FightSettleResponse>(nameof(FightSettleRequest), settle).Code,
                    $"Manual task {taskId} real fight settlement");
                return settle;
            }

            Fight(matchingId, false, forceExit: true);
            AssertEqual(0, Progress(), "Failed matching-character fight cannot count participation");
            if (!arena) AssertEqual(true, Save().Code != 0, "Force-exit cannot authorize Pain Cage score commit");
            if (!arena)
            {
                Fight(matchingId, false);
                AssertEqual(0, Save().Code, "Pain Cage death score is legitimately saveable");
                AssertEqual(0, Progress(), "Saved losing score cannot count winning character participation");
            }
            Fight(otherId, true);
            if (!arena) AssertEqual(0, Save().Code, "Other-character Pain Cage score commits normally");
            AssertEqual(0, Progress(), "Owning target character without deploying it cannot complete participation");
            AssertEqual(true, Request<FinishTaskResponse>(nameof(FinishTaskRequest), new FinishTaskRequest { TaskId = taskId }).Code != 0,
                "Nonmatching clears cannot authorize manual reward");
            for (int count = 1; count <= task.Result; count++)
            {
                FightSettleRequest settle = Fight(matchingId, true);
                if (!arena)
                {
                    AssertEqual(count - 1, Progress(), "Uncommitted Pain Cage score cannot count participation");
                    AssertEqual(0, Save().Code, "Matching-character Pain Cage commit succeeds");
                    AssertEqual(true, Save().Code != 0, "Repeated Pain Cage save has no pending result");
                }
                else
                {
                    _ = Request<FightSettleResponse>(nameof(FightSettleRequest), settle);
                }
                AssertEqual(count, Progress(), "Only accepted matching participation increments once despite retry");
            }
            harness.Session.player = player = BsonSerializer.Deserialize<Player>(saved.LastReplacement!.ToBson());
            AssertEqual(task.Result, Progress(), "Participation counter survives persisted BSON reload");
            var claim = Request<FinishTaskResponse>(nameof(FinishTaskRequest), new FinishTaskRequest { TaskId = taskId });
            AssertEqual(0, claim.Code, "Natural participation reward claim");
            var reward = TableReaderV2.Parse<RewardTable>().Single(row => row.Id == task.RewardId);
            string Signature(IEnumerable<RewardGoods> values) => string.Join(";", values.OrderBy(row => row.TemplateId)
                .Select(row => $"{row.TemplateId}:{row.Count}"));
            AssertEqual(Signature(TableReaderV2.Parse<RewardGoodsTable>().Where(row => reward.SubIds.Contains(row.Id))
                .Select(row => new RewardGoods { TemplateId = row.TemplateId, Count = row.Count })),
                Signature(claim.RewardGoodsList), "Participation task grants authoritative reward");
            harness.Session.player = player = BsonSerializer.Deserialize<Player>(saved.LastReplacement!.ToBson());
            string inventoryBefore = harness.Session.inventory.ToJson();
            AssertEqual(4, BuildTaskData(harness.Session).Single(row => row.Id == taskId).State, "Participation claim survives relog");
            AssertEqual(true, Request<FinishTaskResponse>(nameof(FinishTaskRequest), new FinishTaskRequest { TaskId = taskId }).Code != 0,
                "Relogged participation claim retry rejects");
            AssertEqual(inventoryBefore, harness.Session.inventory.ToJson(), "Participation claim retry cannot duplicate rewards");
        }
    }

    private static void ValidateWheelchairManualPowerTaskCompatibility()
    {
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForStoryDeployVersionGapCompatibility(out var saved);
        const long uid = 50_800;
        var task = TableReaderV2.Parse<CurrentTaskTable>().Single(row => row.Id == 7603);
        var condition = TableReaderV2.Parse<ConditionTable>().Single(row => row.Id == task.Condition);
        int threshold = condition.Params[4];
        var characterRow = TableReaderV2.Parse<CharacterTable>().First(row => row.Type == 1
            && row.EquipId > 0 && Character.IsOwnableCharacter((uint)row.Id));
        Player player = CreateDrawCompatibilityPlayer(uid);
        player.MissionProgress = new();
        Character roster = CreateDrawCompatibilityCharacter(uid);
        var owned = roster.AddCharacter((uint)characterRow.Id).Character;
        Inventory inventory = CreateDrawCompatibilityInventory(uid, []);
        using LoopbackSessionHarness harness = new(roster, player, inventory, "manual-calculated-power");
        harness.Session.stage = CreateLoginAccountCompatibilityStage(uid);
        LoginTask Task() => BuildTaskData(harness.Session).Single(row => row.Id == task.Id);
        long Power() => InvokePrivateStaticWithArgs<long>(RequiredAscNetGameServerType("AscNet.GameServer.Game.CharacterPower"),
            "Calculate", [harness.Session, harness.Session.character.Characters.Single(row => row.Id == owned.Id)]);
        owned.Ability = int.MaxValue;
        AssertEqual(true, Power() < threshold, "Fresh power fixture starts below the configured computed-power requirement");
        AssertEqual(false, Task().State == 3, "Forged cached ability cannot achieve real2500BP requirement");
        owned.Ability = 0;
        int packetId = 50_800;
        T Request<T>(string name, object request)
        {
            int id = packetId++;
            InvokeRegisteredRequestHandler(name, harness.Session, id, request);
            T response = (T)ReadResponsePayload(harness, id, typeof(T).Name, name, typeof(T), maxPacketsToRead: 64);
            AssertNoAvailablePacket(harness, $"{name} changes precede response");
            return response;
        }
        var itemRows = TableReaderV2.Parse<ItemTable>();
        int targetLevel = Math.Min((int)player.PlayerData.Level, Character.characterLevelUpTemplates
            .Where(row => row.Type == characterRow.LevelUpTemplateId).Max(row => row.Level));
        int exp = Character.characterLevelUpTemplates.Where(row => row.Type == characterRow.LevelUpTemplateId
            && row.Level >= owned.Level && row.Level < targetLevel).Sum(row => row.Exp);
        var food = itemRows.Where(row => row.GetCharacterExp(characterRow.Type) > 0)
            .OrderByDescending(row => row.GetCharacterExp(characterRow.Type)).First();
        int count = (exp + food.GetCharacterExp(characterRow.Type) - 1) / food.GetCharacterExp(characterRow.Type);
        inventory.Do(food.Id, count);
        AssertEqual(0, Request<CharacterLevelUpResponse>(nameof(CharacterLevelUpRequest), new CharacterLevelUpRequest
            { TemplateId = owned.Id, UseItems = new() { [food.Id] = count } }).Code, "Power fixture upgrades character through real handler");
        AssertEqual(targetLevel, owned.Level, "Power fixture consumes enough character food to reach its legal level cap");
        var equipRows = TableReaderV2.Parse<AscNet.Table.V2.share.equip.EquipTable>();
        foreach (var memory in equipRows.Where(row => row.Site is >= 1 and <= 6 && row.CharacterType == characterRow.Type
            && (row.CharacterId == 0 || row.CharacterId == owned.Id) && Character.IsOwnableEquipTemplate(row)).GroupBy(row => row.Site)
            .Select(site => site.OrderByDescending(row => row.Quality).ThenBy(row => row.Id).First()))
        {
            var equip = roster.AddEquip((uint)memory.Id)!;
            AssertEqual(0, Request<EquipPutOnResponse>(nameof(EquipPutOnRequest),
                new EquipPutOnRequest { CharacterId = (int)owned.Id, EquipId = checked((int)equip.Id), Site = memory.Site }).Code, "Power fixture equips owned memory through real handler");
        }
        foreach (var equip in roster.Equips.Where(row => row.CharacterId == owned.Id).ToArray())
        {
            var template = equipRows.Single(row => row.Id == equip.TemplateId);
            var progression = Character.ResolveEquipBreakThrough(equip.TemplateId, equip.Breakthrough)!;
            int equipmentExp = Character.equipLevelUpTemplates.Where(row => row.TemplateId == progression.LevelUpTemplateId
                && row.Level >= equip.Level && row.Level < progression.LevelLimit).Sum(row => row.Exp);
            var equipmentFood = itemRows.Where(row => row.GetEquipUpgradeInfo().Exp > 0
                && row.SubTypeParams.FirstOrDefault() == (template.Site == 0 ? 1 : 2))
                .OrderByDescending(row => row.GetEquipUpgradeInfo().Exp).First();
            int amount = (equipmentExp + equipmentFood.GetEquipUpgradeInfo().Exp - 1) / equipmentFood.GetEquipUpgradeInfo().Exp;
            inventory.Do(equipmentFood.Id, amount);
            inventory.Do(Inventory.Coin, equipmentFood.GetEquipUpgradeInfo().Cost * amount);
            AssertEqual(0, Request<EquipLevelUpResponse>(nameof(EquipLevelUpRequest),
                new EquipLevelUpRequest { EquipId = checked((int)equip.Id), UseItems = new() { [equipmentFood.Id] = amount } }).Code,
                "Power fixture enhances equipment through real handler");
            AssertEqual(progression.LevelLimit, equip.Level, "Power fixture reaches the current equipment breakthrough level cap");
        }
        // Level-one skills and unbroken equipment need not reach the power requirement, even at character level 80.
        var skillUpgrades = TableReaderV2.Parse<CharacterSkillUpgradeTable>();
        foreach (int groupId in TableReaderV2.Parse<CharacterSkillTable>().Single(row => row.CharacterId == owned.Id).SkillGroupId.Where(id => id != 0))
        {
            if (Power() >= threshold)
                break;
            var skills = owned.SkillList.Where(skill => Character.ResolveCharacterSkillIdsForGroupId(groupId).Contains(skill.Id)).ToArray();
            if (skills.Length == 0)
                continue;
            int countToUpgrade = skills.Min(skill => skillUpgrades.Where(row => row.SkillId == skill.Id && row.Level >= skill.Level)
                .OrderBy(row => row.Level).TakeWhile(row => row.Level < Character.CharacterSkillMaxLevel((int)skill.Id)
                    && (row.UseCoin.GetValueOrDefault() > 0 || row.UseSkillPoint.GetValueOrDefault() > 0)
                    && Character.MeetsCharacterSkillCondition(owned, row.ConditionId, [])).Count());
            if (countToUpgrade == 0)
                continue;
            var costs = roster.UpgradeCharacterSkillGroup(groupId, countToUpgrade, []);
            inventory.Do(Inventory.Coin, costs.CoinCost);
            inventory.Do(Inventory.SkillPoint, costs.SkillPointCost);
            AssertEqual(0, Request<CharacterUpgradeSkillGroupResponse>(nameof(CharacterUpgradeSkillGroupRequest),
                new CharacterUpgradeSkillGroupRequest { SkillGroupId = groupId, Count = countToUpgrade }).Code,
                "Power fixture upgrades eligible skills through real handler with table-derived costs");
        }
        foreach (var grade in TableReaderV2.Parse<CharacterGradeTable>()
            .Where(row => row.CharacterId == owned.Id).OrderBy(row => row.Grade))
        {
            if (Power() >= threshold)
                break;
            if (grade.Grade != owned.Grade
                || !TableReaderV2.Parse<CharacterGradeTable>().Any(row => row.CharacterId == owned.Id && row.Grade > grade.Grade))
                continue;
            if (grade.UseItemKey is int itemId && grade.UseItemCount is int cost && cost > 0)
                inventory.Do(itemId, cost);
            AssertEqual(0, Request<CharacterPromoteGradeResponse>(nameof(CharacterPromoteGradeRequest),
                new CharacterPromoteGradeRequest { TemplateId = checked((int)owned.Id) }).Code,
                "Power fixture promotes grade through real handler with table-derived costs");
        }
        owned.Ability = 0;
        long computedPower = Power();
        AssertEqual(true, computedPower >= threshold,
            $"Power fixture computed {computedPower} BP, requires {threshold}; character {owned.Id}, level {owned.Level}, quality {owned.Quality}, grade {owned.Grade}");
        AssertEqual(3, Task().State, "Real upgraded attributes achieve2500BP despite zero cached ability");
        roster.Save();
        player.Save();
        harness.Session.character = BsonSerializer.Deserialize<Character>(roster.ToBson());
        harness.Session.player = player = BsonSerializer.Deserialize<Player>(saved.LastReplacement!.ToBson());
        AssertEqual(3, Task().State, "Real power task survives BSON reload without cached ability");
        AssertEqual(0, Request<FinishTaskResponse>(nameof(FinishTaskRequest), new FinishTaskRequest { TaskId = task.Id }).Code,
            "Naturally earned power task can claim its reward");
        harness.Session.player = player = BsonSerializer.Deserialize<Player>(saved.LastReplacement!.ToBson());
        AssertEqual(4, Task().State, "Natural power task claim survives persisted BSON reload");
        string inventoryBefore = inventory.ToJson();
        AssertEqual(true, Request<FinishTaskResponse>(nameof(FinishTaskRequest), new FinishTaskRequest { TaskId = task.Id }).Code != 0,
            "Relogged power task cannot claim twice");
        AssertEqual(inventoryBefore, inventory.ToJson(), "Power task retry cannot duplicate rewards");
        ValidateWheelchairManual8031PowerBoundary();
    }

    private static void ValidateWheelchairManual8031PowerBoundary()
    {
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForStoryDeployVersionGapCompatibility(out var saved);
        const long uid = 50_900;
        var task = TableReaderV2.Parse<CurrentTaskTable>().Single(row => row.Id == 8031);
        var condition = TableReaderV2.Parse<ConditionTable>().Single(row => row.Id == task.Condition);
        int threshold = condition.Params[4];
        var template = TableReaderV2.Parse<CharacterTable>().Single(row => row.Id == condition.Params[0]);
        Player player = CreateDrawCompatibilityPlayer(uid);
        player.MissionProgress = new();
        Character roster = CreateDrawCompatibilityCharacter(uid);
        var member = roster.AddCharacter((uint)template.Id).Character;
        using LoopbackSessionHarness harness = new(roster, player, CreateDrawCompatibilityInventory(uid, []), "manual-8031-boundary");
        harness.Session.stage = CreateLoginAccountCompatibilityStage(uid);
        long Power() => InvokePrivateStaticWithArgs<long>(RequiredAscNetGameServerType("AscNet.GameServer.Game.CharacterPower"),
            "Calculate", [harness.Session, member]);
        LoginTask Task() => BuildTaskData(harness.Session).Single(row => row.Id == task.Id);
        int packetId = 50_900;
        FinishTaskResponse Claim()
        {
            int id = packetId++;
            InvokeRegisteredRequestHandler(nameof(FinishTaskRequest), harness.Session, id, new FinishTaskRequest { TaskId = task.Id });
            return (FinishTaskResponse)ReadResponsePayload(harness, id, nameof(FinishTaskResponse), "manual8031 claim",
                typeof(FinishTaskResponse), maxPacketsToRead: 64);
        }
        member.Ability = int.MaxValue;
        AssertEqual(true, Power() < threshold, "Fresh owned target is genuinely below table power");
        AssertEqual(1, Task().State, "8031 rejects inflated cached Ability");
        AssertEqual(true, Claim().Code != 0, "8031 cannot claim below computed power threshold");
        member.Ability = 0;

        // Build exact adjacent powers from legal table gear/skill levels, not cached Ability or edited tables.
        member.Level = Character.characterLevelUpTemplates.Where(row => row.Type == template.LevelUpTemplateId).Max(row => row.Level);
        member.Grade = TableReaderV2.Parse<CharacterGradeTable>().Where(row => row.CharacterId == member.Id).Max(row => row.Grade);
        roster.Equips.Clear();
        var equipRows = TableReaderV2.Parse<EquipTable>();
        for (int site = 0; site <= 6; site++)
        {
            var equipTemplate = equipRows.Where(row => row.Site == site && Character.IsOwnableEquipTemplate(row)
                && (site == 0 ? row.Type == template.EquipType : row.CharacterType == template.Type))
                .OrderByDescending(row => row.Quality).ThenBy(row => row.Id).First();
            var equip = roster.AddEquip((uint)equipTemplate.Id, (int)member.Id)!;
            var breakthrough = TableReaderV2.Parse<EquipBreakThroughTable>()
                .Where(row => row.EquipId == equip.TemplateId).MaxBy(row => row.Times)!;
            equip.Breakthrough = breakthrough.Times;
            equip.Level = breakthrough.LevelLimit;
        }
        var effects = TableReaderV2.Parse<CharacterSkillLevelEffectTable>();
        var upgrades = TableReaderV2.Parse<CharacterSkillUpgradeTable>();
        Dictionary<long, int[]> skillPowers = new() { [0] = [] };
        foreach (var skill in member.SkillList)
        {
            var options = effects.Where(row => row.SkillId == skill.Id && (row.Level == 1
                || upgrades.Any(upgrade => upgrade.SkillId == skill.Id && upgrade.Level == row.Level)));
            Dictionary<long, int[]> next = new();
            foreach (var option in options)
                foreach (var previous in skillPowers)
                    next.TryAdd(previous.Key + (option.Ability ?? 0), [.. previous.Value, option.Level]);
            skillPowers = next;
            skill.Level = 1;
        }
        long initialSkillPower = member.SkillList.Sum(skill => (long)(effects.Single(row => row.SkillId == skill.Id && row.Level == 1).Ability ?? 0));
        Dictionary<long, byte[]> fixtures = new();
        var weapon = roster.Equips.Single(row => equipRows.Single(templateRow => templateRow.Id == row.TemplateId).Site == 0);
        var memories = roster.Equips.Where(row => row != weapon).ToArray();
        foreach (var quality in TableReaderV2.Parse<CharacterQualityTable>().Where(row => row.CharacterId == member.Id).OrderByDescending(row => row.Quality))
        {
            member.Quality = quality.Quality;
            member.Star = 0;
            for (int memoryLevel = 1; memoryLevel <= memories.Min(row => Character.ResolveEquipBreakThrough(row.TemplateId, row.Breakthrough)!.LevelLimit); memoryLevel++)
            {
                foreach (var memory in memories) memory.Level = memoryLevel;
                for (int weaponLevel = 1; weaponLevel <= Character.ResolveEquipBreakThrough(weapon.TemplateId, weapon.Breakthrough)!.LevelLimit; weaponLevel++)
                {
                    weapon.Level = weaponLevel;
                    long basePower = Power() - initialSkillPower;
                    foreach (long target in new long[] { threshold - 1, threshold, threshold + 1 })
                    {
                        if (fixtures.ContainsKey(target) || !skillPowers.TryGetValue(target - basePower, out var levels)) continue;
                        for (int index = 0; index < levels.Length; index++) member.SkillList[index].Level = levels[index];
                        AssertEqual(target, Power(), "Authoritative gear and skills produce exact boundary power");
                        fixtures.Add(target, roster.ToBson());
                        foreach (var skill in member.SkillList) skill.Level = 1;
                    }
                    if (fixtures.Count == 3) break;
                }
                if (fixtures.Count == 3) break;
            }
            if (fixtures.Count == 3) break;
        }
        AssertEqual(3, fixtures.Count, "Authoritative data supplies below/at/above 6000 fixtures");
        void Load(long power)
        {
            harness.Session.character = roster = BsonSerializer.Deserialize<Character>(fixtures[power]);
            member = roster.Characters.Single(row => row.Id == template.Id);
            AssertEqual(power, Power(), "Boundary fixture survives BSON reload");
        }
        Load(threshold - 1);
        member.Ability = int.MaxValue;
        AssertEqual(1, Task().State, "5999 computed power remains incomplete with trailing condition fields");
        AssertEqual(true, Claim().Code != 0, "5999 cannot authorize reward");
        Load(threshold + 1);
        AssertEqual(3, Task().State, "6001 computed power achieves8031 with zero cached ability");
        Load(threshold);
        AssertEqual(3, Task().State, "6000 inclusive table threshold achieves8031");
        int EvaluatePrefix(List<int> parameters) => InvokePrivateStaticWithArgs<int>(
            RequiredAscNetGameServerType("AscNet.GameServer.Handlers.TaskModule"), "EvaluateCurrentCondition",
            [harness.Session, new CurrentConditionTable { Id = 987_631, Type = condition.Type ?? throw new InvalidDataException("Missing character condition type."), Params = parameters }, null]);
        List<int> prefix = [(int)member.Id, member.Quality, member.Level, member.Grade, threshold];
        AssertEqual(1, EvaluatePrefix(prefix), "Generic five-field predicate accepts exact known thresholds");
        AssertEqual(1, EvaluatePrefix([.. prefix, 1]), "Sixth field does not reject an otherwise matching known prefix");
        AssertEqual(1, EvaluatePrefix([.. prefix, 77, 88]), "Opaque trailing fields acquire no invented meaning");
        for (int index = 1; index <= 4; index++)
        {
            List<int> stricter = [.. prefix, 77, 88];
            stricter[index]++;
            AssertEqual(0, EvaluatePrefix(stricter), $"Trailing fields cannot bypass known requirement {index}");
        }
        var other = roster.AddCharacter((uint)TableReaderV2.Parse<CharacterTable>().First(row => row.Id != template.Id
            && row.Type == template.Type && Character.IsOwnableCharacter((uint)row.Id)).Id).Character;
        other.Ability = int.MaxValue;
        roster.Characters.Remove(member);
        AssertEqual(1, Task().State, "Wrong owned character cannot satisfy target-specific power");
        AssertEqual(true, Claim().Code != 0, "Wrong character cannot claim8031");
        Load(threshold);
        var claim = Claim();
        AssertEqual(0, claim.Code, "8031 claims at exact computed6000");
        var reward = TableReaderV2.Parse<RewardTable>().Single(row => row.Id == task.RewardId);
        string Signature(IEnumerable<RewardGoods> values) => string.Join(";", values.OrderBy(row => row.TemplateId)
            .Select(row => $"{row.TemplateId}:{row.Count}"));
        AssertEqual(Signature(TableReaderV2.Parse<RewardGoodsTable>().Where(row => reward.SubIds.Contains(row.Id))
            .Select(row => new RewardGoods { TemplateId = row.TemplateId, Count = row.Count })),
            Signature(claim.RewardGoodsList), "8031 grants table-authorized reward");
        harness.Session.player = player = BsonSerializer.Deserialize<Player>(saved.LastReplacement!.ToBson());
        AssertEqual(4, Task().State, "8031 claimed state survives persistence");
        string inventoryBefore = harness.Session.inventory.ToJson();
        AssertEqual(true, Claim().Code != 0, "8031 replay rejects after relog");
        AssertEqual(inventoryBefore, harness.Session.inventory.ToJson(), "8031 replay cannot duplicate rewards");
    }
}
