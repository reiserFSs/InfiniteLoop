using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.bigworld.common;
using AscNet.Table.V2.share.bigworld.common.fovsave;
using AscNet.Table.V2.share.dlcworld;
using AscNet.Table.V2.share.statussyncfight.level;
using AscNet.Table.V2.share.statussyncfight.level.sceneconfig;
using MessagePack;

namespace AscNet.GameServer.Handlers.BigWorld
{
    // Babylonia (client "BigWorld") world lifecycle: enter/leave/instance levels, the engine save channel
    // (DlcWorldSaveData, DlcWorldSceneObjectData, DlcSceneObjectStateSet, DlcWorldEnterSucceed, BigWorldCurNpcPosUpdate),
    // scene-object collection, map box counts, teleporters, guide/fov/custom-param/red-point/map-pin state.
    // Engine snapshots and XRpc live in BigWorldModule.Engine.cs.
    internal static partial class BigWorldModule
    {
        // CodeText 4 InvalidRequest "Invalid request".
        internal const int CodeInvalidRequest = 4;
        // CodeText 25000001 DlcWorldInfoTemplateNotFound "Dlc World information settings do not exist".
        internal const int CodeWorldNotFound = 25000001;
        // CodeText 20290019 BigWorldModuleNotFindLevel "World Mode scene module not found".
        internal const int CodeLevelNotFound = 20290019;
        // CodeText 20292001 BigWorldGuildOpenIdError "World Mode guide ID does not exist".
        internal const int CodeGuideIdError = 20292001;
        // CodeText 20292002 BigWorldGuildSysModuleIdError "World Mode guidance module configuration error".
        internal const int CodeSysModuleIdError = 20292002;
        // CodeText 20292003 BigWorldGuildAlreadyCompleted "World Mode guide completed".
        internal const int CodeGuideAlreadyCompleted = 20292003;
        // CodeText 20292004 BigWorldGuildNotInWorld "Player not in World Mode during World Mode guidance".
        internal const int CodeNotInWorld = 20292004;

        // DlcWorld.WorldType of BigWorld worlds (XBigWorldGamePlayAgency _InitBigWorldType).
        private const int BigWorldWorldType = 4;
        // Level.LevelType 2 = instance level (跳跳乐副本 / story mission copies); 1 = open-world level.
        private const int InstanceLevelType = 2;
        // XBigWorldInstanceAgency.LevelSaveOption.
        private const int InstSaveOptionNone = 0;
        private const int InstSaveOptionSaveExit = 1;
        private const int InstSaveOptionNoSaveExit = 2;
        // XEnumConst.BWMap.TrackOperator.
        private const int TrackOperatorBegin = 1;
        private const int TrackOperatorFinish = 3;

        private static readonly Lazy<Dictionary<int, DlcWorldTable>> BigWorlds = new(() => TableReaderV2.Parse<DlcWorldTable>()
            .Where(row => row.WorldType == BigWorldWorldType)
            .ToDictionary(row => row.WorldId));
        private static readonly Lazy<Dictionary<int, int>> WorldDefaultLevels = new(() => TableReaderV2.Parse<WorldTable>()
            .ToDictionary(row => row.Id, row => row.DefaultLevel));
        internal static readonly Lazy<Dictionary<int, LevelTable>> Levels = new(() => TableReaderV2.Parse<LevelTable>()
            .ToDictionary(row => row.Id));
        private static readonly Lazy<Dictionary<string, string>> Config = new(() => TableReaderV2.Parse<BigWorldConfigTable>()
            .ToDictionary(row => row.Key, row => row.Value));
        // Level config groups live in the sector bundle sceneconfig/<Level.SectorName lower>.ab; group ids repeat across
        // sectors (ConfigGroup_5001: District B in skygarden, Commandant's Lounge in skygarden_sushe), so rows key on both.
        internal static (string Sector, int GroupId) GroupKey(LevelTable level, int groupId) => ((level.SectorName ?? string.Empty).ToLowerInvariant(), groupId);

        // levelId -> placeId -> native scene object (Level.SectorName + ConfigGroups -> LevelSceneObject.Sector + ConfigGroupId).
        private static readonly Lazy<Dictionary<int, Dictionary<int, LevelSceneObjectTable>>> LevelSceneObjects = new(() =>
        {
            ILookup<(string, int), LevelSceneObjectTable> byGroup = TableReaderV2.Parse<LevelSceneObjectTable>().ToLookup(row => (row.Sector, row.ConfigGroupId));
            return Levels.Value.Values.ToDictionary(
                level => level.Id,
                level => level.ConfigGroups.SelectMany(groupId => byGroup[GroupKey(level, groupId)])
                    .GroupBy(row => row.PlaceId)
                    .ToDictionary(group => group.Key, group => group.First()));
        });

        internal static bool IsBigWorld(int worldId) => BigWorlds.Value.ContainsKey(worldId) && WorldDefaultLevels.Value.ContainsKey(worldId);

        internal static int ConfigInt(string key) => int.Parse(Config.Value[key]);

        // AscNet policy: no table links Level to World. A BigWorld world owns the levels whose Level.SectorName
        // starts with the SectorName of its World.DefaultLevel (400/4001 "SkyGarden" -> SkyGarden*, 405/4005 "SkyGarden_Plaza").
        internal static bool IsWorldLevel(int worldId, int levelId)
        {
            if (!IsBigWorld(worldId)
                || !Levels.Value.TryGetValue(levelId, out LevelTable? level)
                || !Levels.Value.TryGetValue(WorldDefaultLevels.Value[worldId], out LevelTable? defaultLevel)
                || string.IsNullOrEmpty(defaultLevel.SectorName))
                return false;
            return level.SectorName?.StartsWith(defaultLevel.SectorName, StringComparison.Ordinal) == true;
        }

        private static bool IsInstanceLevel(int levelId) => Levels.Value.TryGetValue(levelId, out LevelTable? level) && level.LevelType == InstanceLevelType;

        // BigWorldLevelPlay rows of XBigWorldInstanceAgency.LevelPlayType.JumpPlay (1) with a quest: Space Leap and the other jumper
        // challenges. Their engine progress is never saved (Level.ShouldSaveInstLevelProgress 0) and the settle UI offers
        // DlcReChallengeInst, so every visit is a run of its own.
        private static readonly Lazy<Dictionary<int, int>> JumpPlayQuests = new(() => TableReaderV2.Parse<BigWorldLevelPlayTable>()
            .Where(row => row.Type == 1 && row.QuestId > 0).GroupBy(row => row.LevelId).ToDictionary(group => group.Key, group => group.First().QuestId!.Value));

        internal static int JumpPlayQuestOf(int levelId) => JumpPlayQuests.Value.GetValueOrDefault(levelId);

        // AscNet policy [INF] (no retail capture of an instance replay): entering or re-challenging a jumper level, or entering an
        // invite instance level (BigWorldQuestModule.InviteLevelQuests), discards the previous run - its actor overrides and engine
        // save, the finished flag and the level quest (restarted Ready, FinishedQuests keeps the history).
        // Live: the stale run of quest 5001 was relaunched on re-entry and its pending settle list ran at once.
        internal static void StartPlayRun(Session session, int levelId)
        {
            List<int> questIds = JumpPlayQuestOf(levelId) is > 0 and int jump ? [jump] : BigWorldQuestModule.InviteLevelQuests(levelId);
            if (questIds.Count == 0)
                return;
            BigWorldPlayerState state = session.player.BigWorldState;
            state.ActorOverrides.RemoveAll(o => o.LevelId == levelId);
            state.SceneObjectStates.Remove(levelId);
            state.InstLevelFinished = false;
            session.player.Save();
            foreach (int questId in questIds)
                BigWorldQuestRuntime.RestartPlayQuest(session, questId, !IsOnline);
        }

        internal static IReadOnlyDictionary<int, LevelSceneObjectTable> SceneObjectsOf(int levelId) =>
            LevelSceneObjects.Value.TryGetValue(levelId, out Dictionary<int, LevelSceneObjectTable>? objects) ? objects : new Dictionary<int, LevelSceneObjectTable>();

        internal static int CurrentLevelId(BigWorldPlayerState state) => state.InstLevelId != 0 ? state.InstLevelId : state.LastLevelId;

        // Collected collectable scene objects (CollectableComponent.RewardId > 0) in a level.
        internal static int CountCollected(Player player, int levelId)
        {
            IReadOnlyDictionary<int, LevelSceneObjectTable> objects = SceneObjectsOf(levelId);
            return player.BigWorldState.ClaimedSceneObjects
                .Where(claim => claim.LevelId == levelId && objects.TryGetValue(claim.PlaceId, out LevelSceneObjectTable? row) && row.CollectRewardId > 0)
                .Select(claim => claim.PlaceId)
                .Distinct()
                .Count();
        }

        // Collected collectables whose CollectableComponent.CourseGroupId (= BigWorldCourseExplorePoi.Id) matches.
        internal static int CollectedByCourseGroup(Player player, int courseGroupId) =>
            player.BigWorldState.ClaimedSceneObjects
                .Where(claim => SceneObjectsOf(claim.LevelId).TryGetValue(claim.PlaceId, out LevelSceneObjectTable? row)
                    && row.CollectRewardId > 0 && row.CourseGroupId == courseGroupId)
                .Select(claim => (claim.LevelId, claim.PlaceId))
                .Distinct()
                .Count();

        // Grants a BigWorldReward and dispatches the slice-owned goods to their owners.
        internal static List<RewardGoods> GrantReward(Session session, int bigWorldRewardId)
        {
            List<RewardGoods> goods = BigWorldRewardService.Grant(session, bigWorldRewardId);
            BigWorldCharacterModule.ApplySliceRewards(session, goods);
            BigWorldArchiveModule.ApplySliceRewards(session, goods);
            SkyGardenDormModule.ApplySliceRewards(session, goods);
            BigWorldQuestModule.ApplySliceRewards(session, goods);
            BigWorldTaskModule.OnProgressChanged(session);
            return goods;
        }

        #region Enter / leave

        [RequestPacketHandler("BigWorldEnterWorldRequest")]
        public static void BigWorldEnterWorldRequestHandler(Session session, Packet.Request packet)
        {
            BigWorldEnterWorldRequest request = packet.Deserialize<BigWorldEnterWorldRequest>();
            BigWorldPlayerState state = session.player.BigWorldState;

            // Retail sends {WorldId 0, LevelId 0} from the entrance: resume the last world, else BigWorldConfig.FirstEnterDefaultWorldId.
            int worldId = request.WorldId > 0 ? request.WorldId
                : state.LastWorldId > 0 ? state.LastWorldId
                : ConfigInt("FirstEnterDefaultWorldId");
            if (!IsBigWorld(worldId))
            {
                session.SendResponse(new BigWorldEnterWorldResponse { Code = CodeWorldNotFound }, packet.Id);
                return;
            }

            // AscNet policy [INF]: the entrance request (LevelId 0) resumes an unfinished instance of the last world at its
            // saved pose only when the level keeps its progress (Level.ShouldSaveInstLevelProgress 1); the open-world pose
            // (LastLevelId/LastPosition) stays untouched for when the instance is left. No level of the installed Level table
            // sets the flag, so every instance is left on a relog: retail does not save the run, and a resumed run skipped the
            // trial-team swap after the intro drama (4033: XGameplayHeavyArtilleryFire.OnPlayerNpcChanged turns the aim input map
            // on) that only the quest flow performs. Retail evidence for instances is absent (babylonia.pcap is district 4001
            // only); the client takes WorldData.LevelId as is (XBigWorldGamePlayProcess.lua OnEnterWorldResponse).
            bool resumeInstance = request.LevelId <= 0 && state.InstLevelId != 0 && state.LastWorldId == worldId && IsWorldLevel(worldId, state.InstLevelId)
                && JumpPlayQuestOf(state.InstLevelId) == 0 && SavesInstanceProgress(state.InstLevelId);
            int levelId = request.LevelId;
            if (resumeInstance)
                levelId = state.InstLevelId;
            else if (levelId > 0)
            {
                if (!IsWorldLevel(worldId, levelId) || IsInstanceLevel(levelId))
                {
                    session.SendResponse(new BigWorldEnterWorldResponse { Code = CodeLevelNotFound }, packet.Id);
                    return;
                }
            }
            else
            {
                levelId = state.LastWorldId == worldId && state.LastLevelId > 0 && IsWorldLevel(worldId, state.LastLevelId) && !IsInstanceLevel(state.LastLevelId)
                    ? state.LastLevelId
                    : WorldDefaultLevels.Value[worldId];
            }

            if (!resumeInstance)
            {
                if (state.LastWorldId != worldId || state.LastLevelId != levelId)
                {
                    // A different open-world location invalidates the persisted spawn pose.
                    state.LastPosition = null;
                    state.LastRotation = null;
                    state.LastRotationY = null;
                }
                state.LastWorldId = worldId;
                state.LastLevelId = levelId;
                // AscNet policy: an explicit open-world entry leaves an unfinished instance the same way as LeaveInstLevel None.
                if (state.InstLevelId != 0)
                {
                    int left = state.InstLevelId;
                    bool finished = state.InstLevelFinished;
                    LeaveInstance(session.player, InstSaveOptionNone);
                    DiscardUnsavedRun(session, left, finished);
                }
            }
            bool newWorld = !state.EnteredWorldIds.Contains(worldId);
            if (newWorld)
                state.EnteredWorldIds.Add(worldId);
            // The level is announced through PlayerData.EnteredLevelIds: XBigWorldMapModel._UnlockLevelMap does not exist until
            // BigWorldOnModuleLoadComplete runs UpdatePlayerData, so a NotifyNewEnteredBigWorldLevelId pushed here would throw.
            MarkLevelEntered(state, levelId);
            session.BigWorldWorldId = worldId;
            session.player.Save();

            BigWorldEnterWorldResponse response = new()
            {
                EnterResultData = BuildEnterResultData(session, worldId, levelId),
                PlayerData = BuildPlayerData(session.player),
                DlcQuestBag = BigWorldQuestModule.BuildQuestBag(session.player)
            };
            session.SendResponse(response, packet.Id);

            BigWorldArchiveModule.SendEnterPushes(session);
            session.SendPush(BuildMapData(session.player, worldId));
            SkyGardenDormModule.SendDormData(session);
            SkyGardenCafeModule.SendCafeData(session);
            SkyGardenDroneModule.SendDroneData(session);
            SkyGardenStreetModule.SendWorldEnterPushes(session);
            if (newWorld)
                session.SendPush(new NotifyNewEnteredBigWorldId { WorldId = worldId });
            BigWorldQuestModule.OnLevelEntered(session, levelId);
        }

        [RequestPacketHandler("BigWorldGetEnterWorldDataRequest")]
        public static void BigWorldGetEnterWorldDataRequestHandler(Session session, Packet.Request packet)
        {
            BigWorldPlayerState state = session.player.BigWorldState;
            if (session.BigWorldWorldId == 0)
            {
                session.SendResponse(new BigWorldGetEnterWorldDataResponse { Code = CodeNotInWorld }, packet.Id);
                return;
            }
            session.SendResponse(new BigWorldGetEnterWorldDataResponse
            {
                EnterResultData = BuildEnterResultData(session, session.BigWorldWorldId, CurrentLevelId(state)),
                PlayerData = BuildPlayerData(session.player),
                DlcQuestBag = BigWorldQuestModule.BuildQuestBag(session.player)
            }, packet.Id);
        }

        [RequestPacketHandler("BigWorldOnModuleLoadCompleteRequest")]
        public static void BigWorldOnModuleLoadCompleteRequestHandler(Session session, Packet.Request packet)
        {
            // XBigWorldGamePlayProcess.lua:67-99: failure aborts the enter; only valid after BigWorldEnterWorld.
            session.SendResponse(new BigWorldOnModuleLoadCompleteResponse { Code = session.BigWorldWorldId == 0 ? CodeNotInWorld : 0 }, packet.Id);
        }

        // WorldModule LeaveWorldRequest: flush and detach the session from BigWorld.
        internal static void OnLeaveWorld(Session session)
        {
            BigWorldLevelActions.Reset(session);
            if (session.BigWorldWorldId == 0)
                return;
            BigWorldQuestRuntime.OnPlayerLeaveLevel(session);
            session.BigWorldWorldId = 0;
            session.PendingBigWorldLoadCompleteXRpc = false;
            session.PendingBigWorldStartFightNotify = false;
            session.player.Save();
        }

        private static bool MarkLevelEntered(BigWorldPlayerState state, int levelId)
        {
            if (state.EnteredLevelIds.Contains(levelId))
                return false;
            state.EnteredLevelIds.Add(levelId);
            return true;
        }

        #endregion

        #region Instance levels

        [RequestPacketHandler("EnterInstLevelRequest")]
        public static void EnterInstLevelRequestHandler(Session session, Packet.Request packet)
        {
            EnterInstLevelRequest request = packet.Deserialize<EnterInstLevelRequest>();
            // The engine's in-world transfer (CMD_REQUEST_ENTER_INST_LEVEL -> XBigWorldGamePlayAgency.CmdRequestEnterInstLevel)
            // sends WorldId = data.WorldId or 0: 0 means the current world.
            int from = CurrentLevelId(session.player.BigWorldState);
            int code = TransferLevel(session, request.WorldId, request.InstLevelId, request.TargetPos, request.TargetRot, out bool newLevel);
            if (code != 0)
            {
                session.SendResponse(new EnterInstLevelResponse { Code = code }, packet.Id);
                return;
            }
            // In-world quest activation goes out over XRpc (RpcActivateQuests) before the response: BuildEnterResultData's
            // silent re-evaluation would announce it with NotifyDlcQuestActivate, which the 4.8 Lua does not handle (live:
            // quest 5001 activated on entering 4008, then "HandleQuestUpdate 找不到任务对象 [questId:5001]").
            BigWorldQuestRuntime.OnConditionsChanged(session);
            session.SendResponse(new EnterInstLevelResponse
            {
                EnterResultData = BuildEnterResultData(session, session.BigWorldWorldId, request.InstLevelId, request.TargetPos, request.TargetRot)
            }, packet.Id);
            // The client only hands EnterResultData to an optional callback (XBigWorldGamePlayAgency:RequestEnterInstLevel);
            // like the RequestEnterInstLevel level action, the server moves it with RpcPlayerSwitchLevelNotify.
            BigWorldLevelSwitch.Send(session, from, request.InstLevelId, request.TargetPos, request.TargetRot);
            AfterLevelTransfer(session, request.InstLevelId, newLevel);
        }

        // In-world level transfer shared by EnterInstLevelRequest and the RequestEnterInstLevel/SwitchLevel level actions.
        // worldId 0 = the current world. Returns 0 or the response code; newLevel = first visit of the level.
        internal static int TransferLevel(Session session, int worldId, int levelId, Theatre5DlcVector3? targetPos, Theatre5DlcVector3? targetRot, out bool newLevel)
        {
            newLevel = false;
            BigWorldPlayerState state = session.player.BigWorldState;
            worldId = worldId == 0 ? session.BigWorldWorldId : worldId;
            if (session.BigWorldWorldId == 0 || worldId != session.BigWorldWorldId)
                return CodeNotInWorld;
            // AscNet policy: level scripts use this request for every in-world level transfer; any level of the world is
            // accepted, and only Level.LevelType 2 levels are tracked as instances (open-world targets move LastLevelId).
            if (!IsWorldLevel(worldId, levelId))
                return CodeLevelNotFound;
            // The level the player already stands on (a quest list moved the server state before the client's own request
            // arrived) is a re-entry: only the pose moves; the caller still sends the switch, which the client loads as
            // ReenterLevel (EFightState 6, see DlcReChallengeInst) [LUA XBigWorldGamePlayAgency:RequestEnterInstLevel].
            if (levelId == CurrentLevelId(state))
            {
                if (targetPos is not null && IsInstanceLevel(levelId))
                {
                    state.InstPosition = ToVector(targetPos);
                    state.InstRotationY = targetRot?.Y;
                }
                else if (targetPos is not null)
                {
                    state.LastPosition = ToVector(targetPos);
                    state.LastRotation = null;
                    state.LastRotationY = targetRot?.Y;
                }
                session.player.Save();
                return 0;
            }

            if (state.InstLevelId != 0)
                LeaveInstance(session.player, InstSaveOptionNone);
            if (IsInstanceLevel(levelId))
            {
                state.InstLevelId = levelId;
                state.InstPosition = ToVector(targetPos);
                state.InstRotationY = targetRot?.Y;
                StartPlayRun(session, levelId);
            }
            else
            {
                state.LastLevelId = levelId;
                state.LastPosition = ToVector(targetPos);
                state.LastRotation = null;
                state.LastRotationY = targetRot?.Y;
            }
            newLevel = MarkLevelEntered(state, levelId);
            session.player.Save();
            return 0;
        }

        internal static void AfterLevelTransfer(Session session, int levelId, bool newLevel)
        {
            if (newLevel)
                session.SendPush(new NotifyNewEnteredBigWorldLevelId { LevelId = levelId });
            BigWorldQuestModule.OnLevelEntered(session, levelId);
        }

        [RequestPacketHandler("LeaveInstLevelRequest")]
        public static void LeaveInstLevelRequestHandler(Session session, Packet.Request packet)
        {
            LeaveInstLevelRequest request = packet.Deserialize<LeaveInstLevelRequest>();
            BigWorldPlayerState state = session.player.BigWorldState;
            int from = state.InstLevelId;
            int code = LeaveInstanceLevel(session, request.InstSaveOption, request.InstSaveOption == InstSaveOptionNoSaveExit);
            session.SendResponse(new LeaveInstLevelResponse { Code = code }, packet.Id);
            // The client callback does nothing (XBigWorldGamePlayAgency:RequestLeaveInstLevel): the server moves the player
            // back to the open world like the RequestLeaveInstLevel level action (saved open-world pose).
            if (code != 0 || session.BigWorldWorldId == 0)
                return;
            (Theatre5DlcVector3? pos, Theatre5DlcVector3? rot) = BigWorldLevelSwitch.OpenWorldPose(state);
            BigWorldLevelSwitch.Send(session, from, state.LastLevelId, pos, rot);
            AfterLevelTransfer(session, state.LastLevelId, false);
        }

        // Shared by LeaveInstLevelRequest and RequestLeaveInstLevel: returns 0 or the response code. rollback discards the
        // instance quests' progress (explicit "exit without saving" only; a finished instance keeps its quests).
        internal static int LeaveInstanceLevel(Session session, int saveOption, bool rollback = false)
        {
            BigWorldPlayerState state = session.player.BigWorldState;
            if (state.InstLevelId == 0)
                return CodeLevelNotFound;
            if (saveOption is not (InstSaveOptionNone or InstSaveOptionSaveExit or InstSaveOptionNoSaveExit))
                return CodeInvalidRequest;
            int left = state.InstLevelId;
            rollback &= !state.InstLevelFinished;
            LeaveInstance(session.player, saveOption);
            session.player.Save();
            if (rollback)
                BigWorldQuestRuntime.OnInstanceRolledBack(session, left);
            else
                BigWorldQuestRuntime.OnInstanceLeft(session, left);
            session.player.Save();
            return 0;
        }

        [RequestPacketHandler("DlcReChallengeInstRequest")]
        public static void DlcReChallengeInstRequestHandler(Session session, Packet.Request packet)
        {
            BigWorldPlayerState state = session.player.BigWorldState;
            int levelId = state.InstLevelId;
            if (levelId == 0)
            {
                session.SendResponse(new DlcReChallengeInstResponse { Code = CodeLevelNotFound }, packet.Id);
                return;
            }
            // Re-challenge restarts the instance: its engine save (scene objects) is discarded.
            state.SceneObjectStates.Remove(levelId);
            if (JumpPlayQuestOf(levelId) == 0)
            {
                session.player.Save();
                session.SendResponse(new DlcReChallengeInstResponse(), packet.Id);
                return;
            }
            // Jumper: a fresh run at the level's start pose. The client's ReenterLevel state (EFightState 6) is what a switch to the
            // level it is already in starts [DUMP48 XFight.HandlePlayerSwitchLevelNotify 0x1F5D3E0: target level == current -> 6, else 3].
            LevelTable level = Levels.Value[levelId];
            Theatre5DlcVector3 pos = new() { X = (float)level.PositionX, Y = (float)level.PositionY, Z = (float)level.PositionZ }, rot = new() { Y = (float)level.RotationY };
            state.InstPosition = ToVector(pos);
            state.InstRotationY = rot.Y;
            StartPlayRun(session, levelId);
            session.SendResponse(new DlcReChallengeInstResponse(), packet.Id);
            BigWorldLevelSwitch.Send(session, levelId, levelId, pos, rot);
            AfterLevelTransfer(session, levelId, false);
        }

        // SaveExit keeps the instance's engine save only when Level.ShouldSaveInstLevelProgress is set; NoSaveExit always
        // drops it; None keeps what the level config asks for. The trial team an instance quest added (AddTrialNpcToTeam: the
        // 4033 commander armor) belongs to the instance, so every exit path (save/no-save exit, RequestLeaveInstLevel, map
        // transfer, explicit world entry) gives the real team and its CurNpcPos back; the level switch or enter snapshot that
        // follows replicates it. AscNet policy [INF]: a trial team started in the open world is not distinguishable here.
        private static void LeaveInstance(Player player, int saveOption)
        {
            BigWorldPlayerState state = player.BigWorldState;
            bool keep = saveOption != InstSaveOptionNoSaveExit && SavesInstanceProgress(state.InstLevelId);
            if (!keep)
                state.SceneObjectStates.Remove(state.InstLevelId);
            state.InstLevelId = 0;
            state.InstPosition = null;
            state.InstRotationY = null;
            state.InstLevelFinished = false;
            if (state.TrialNpcIds.Count > 0)
                BigWorldTrialTeam.Discard(player);
        }

        private static bool SavesInstanceProgress(int levelId) =>
            Levels.Value.TryGetValue(levelId, out LevelTable? level) && level.ShouldSaveInstLevelProgress == 1;

        // A relog leaves an unsaved instance (Level.ShouldSaveInstLevelProgress 0): the run's actor overrides go and, unless the
        // instance was finished, its in-progress instance quests return to Ready like a NoSaveExit, so the next entry replays them
        // from the first objective (intro drama, trial-team swap). The client has no fight yet, hence silent. AscNet policy [INF].
        private static void DiscardUnsavedRun(Session session, int levelId, bool finished)
        {
            if (SavesInstanceProgress(levelId))
                return;
            session.player.BigWorldState.ActorOverrides.RemoveAll(o => o.LevelId == levelId);
            if (!finished)
                BigWorldQuestRuntime.OnInstanceRolledBack(session, levelId, silent: true);
            session.player.Save();
        }

        #endregion

        #region Engine save channel

        [RequestPacketHandler("DlcWorldSaveDataRequest")]
        public static void DlcWorldSaveDataRequestHandler(Session session, Packet.Request packet)
        {
            DlcWorldSaveDataRequest request = packet.Deserialize<DlcWorldSaveDataRequest>();
            BigWorldPlayerState state = session.player.BigWorldState;
            if (session.BigWorldWorldId == 0 || request.WorldId != session.BigWorldWorldId)
            {
                session.SendResponse(new DlcWorldSaveDataResponse { Code = CodeNotInWorld }, packet.Id);
                return;
            }

            BigWorldWorldSaveData saveData = new();
            int currentLevelId = CurrentLevelId(state);
            foreach (int levelId in state.SceneObjectStates.Keys.Where(levelId => IsWorldLevel(request.WorldId, levelId))
                .Append(currentLevelId).Distinct().OrderBy(id => id))
            {
                bool current = levelId == currentLevelId;
                bool openWorldPose = current && state.InstLevelId == 0 && state.LastPosition is not null;
                saveData.LevelDataDict[levelId] = new BigWorldLevelSaveData
                {
                    WorldId = request.WorldId,
                    LevelId = levelId,
                    ReliablePos = openWorldPose ? ToDlcVector(state.LastPosition) : null,
                    ReliableRotationY = openWorldPose ? (float)(GetRotationY(state) ?? 0D) : 0F,
                    LastEnterLevelTimestamp = current ? DateTimeOffset.UtcNow.ToUnixTimeSeconds() : 0,
                    ActorSaveData = new BigWorldLevelActorSaveData { SoSaveDatas = RawStates(state, levelId) }
                };
            }
            session.SendResponse(new DlcWorldSaveDataResponse { WorldSaveData = saveData }, packet.Id);

            // Online engine: the client next enters the fight and sends LoadCompleteRequest (FightModule).
            if (IsOnline)
            {
                session.PendingBigWorldLoadCompleteXRpc = true;
                session.PendingBigWorldStartFightNotify = true;
            }
        }

        [RequestPacketHandler("DlcWorldSceneObjectDataRequest")]
        public static void DlcWorldSceneObjectDataRequestHandler(Session session, Packet.Request packet)
        {
            DlcWorldSceneObjectDataRequest request = packet.Deserialize<DlcWorldSceneObjectDataRequest>();
            if (session.BigWorldWorldId == 0 || request.WorldId != session.BigWorldWorldId || !IsWorldLevel(request.WorldId, request.LevelId))
            {
                session.SendResponse(new DlcWorldSceneObjectDataResponse { Code = CodeLevelNotFound }, packet.Id);
                return;
            }
            session.SendResponse(new DlcWorldSceneObjectDataResponse { SceneObjectStates = RawStates(session.player.BigWorldState, request.LevelId) }, packet.Id);
        }

        private const int TeleporterActiveFlag = 2;

        [RequestPacketHandler("DlcSceneObjectStateSetRequest")]
        public static void DlcSceneObjectStateSetRequestHandler(Session session, Packet.Request packet)
        {
            DlcSceneObjectStateSetRequest request = packet.Deserialize<DlcSceneObjectStateSetRequest>();
            if (session.BigWorldWorldId == 0 || request.WorldId != session.BigWorldWorldId || !IsWorldLevel(request.WorldId, request.LevelId))
            {
                session.SendResponse(new DlcSceneObjectStateSetResponse { Code = CodeLevelNotFound }, packet.Id);
                return;
            }
            IReadOnlyDictionary<int, LevelSceneObjectTable> objects = SceneObjectsOf(request.LevelId);
            Dictionary<int, object> states = request.SceneObjectStates ?? [];
            // Every placeId must be a scene object of the level's native scene config.
            if (states.Count == 0 || states.Any(entry => !objects.ContainsKey(entry.Key) || entry.Value is null))
            {
                session.SendResponse(new DlcSceneObjectStateSetResponse { Code = CodeInvalidRequest }, packet.Id);
                return;
            }

            BigWorldPlayerState state = session.player.BigWorldState;
            if (!state.SceneObjectStates.TryGetValue(request.LevelId, out BigWorldLevelSceneObjects? levelStates))
                state.SceneObjectStates[request.LevelId] = levelStates = new BigWorldLevelSceneObjects();
            List<int> collected = [];
            foreach ((int placeId, object value) in states)
            {
                levelStates.States[placeId] = MessagePackSerializer.Serialize(value);
                // AscNet policy: the engine reports a collected collectable as Active = false (the chest despawns).
                if (objects[placeId].CollectRewardId > 0 && ReadBool(value, "Active") == false)
                    collected.Add(placeId);
                // Client-hosted engine: XSceneObjectSaveData.Flags holds ESceneObjectSaveDataFlag.TeleporterActive (2) once the
                // teleporter component was activated locally [DUMP48 ESceneObjectSaveDataFlag 836830]; mirror it into TeleporterData.
                if (objects[placeId].IsTeleporter == 1 && value is IDictionary<object, object> map && map.TryGetValue("Flags", out object? flags)
                    && flags is IEnumerable<object> flagList && flagList.Any(flag => flag is IConvertible c && c.ToInt32(null) == TeleporterActiveFlag))
                    ActivateTeleporter(session, request.LevelId, placeId);
            }
            session.player.Save();

            List<RewardGoods> rewards = [];
            foreach (int placeId in collected)
                rewards.AddRange(CollectSceneObject(session, request.LevelId, placeId, actorUuid: 0) ?? []);
            session.SendResponse(new DlcSceneObjectStateSetResponse { RewardGoods = rewards }, packet.Id);
        }

        [RequestPacketHandler("DlcWorldEnterSucceedRequest")]
        public static void DlcWorldEnterSucceedRequestHandler(Session session, Packet.Request packet)
        {
            // Native request with no response type (dump.cs D:352780).
            DlcWorldEnterSucceedRequest request = packet.Deserialize<DlcWorldEnterSucceedRequest>();
            BigWorldPlayerState state = session.player.BigWorldState;
            if (session.BigWorldWorldId == 0 || request.WorldId != session.BigWorldWorldId || request.LevelId != state.LastLevelId
                || state.InstLevelId != 0 || request.LastPosition is null)
            {
                session.log.Warn($"BigWorld DlcWorldEnterSucceed ignored: world {request.WorldId} level {request.LevelId} does not match the open-world location.");
                return;
            }
            state.LastPosition = ToVector(request.LastPosition);
            state.LastRotation = null;
            state.LastRotationY = request.LastEulerAngles is null ? null : NormalizeYaw(request.LastEulerAngles.Y);
            session.player.Save();
        }

        [RequestPacketHandler("BigWorldCurNpcPosUpdateRequest")]
        public static void BigWorldCurNpcPosUpdateRequestHandler(Session session, Packet.Request packet)
        {
            BigWorldCurNpcPosUpdateRequest request = packet.Deserialize<BigWorldCurNpcPosUpdateRequest>();
            BigWorldPlayerState state = session.player.BigWorldState;
            int teamSize = BigWorldCharacterModule.BuildWorldNpcList(session.player).Count;
            if (session.BigWorldWorldId == 0 || request.WorldId != session.BigWorldWorldId || request.LevelId != CurrentLevelId(state)
                || request.CurNpcPos < 0 || request.CurNpcPos >= teamSize)
            {
                session.SendResponse(new BigWorldCurNpcPosUpdateResponse { Code = CodeInvalidRequest }, packet.Id);
                return;
            }
            state.CurNpcPos = request.CurNpcPos;
            session.player.Save();
            session.SendResponse(new BigWorldCurNpcPosUpdateResponse(), packet.Id);
        }

        private static Dictionary<int, object> RawStates(BigWorldPlayerState state, int levelId) =>
            state.SceneObjectStates.TryGetValue(levelId, out BigWorldLevelSceneObjects? levelStates)
                ? levelStates.States.ToDictionary(entry => entry.Key, entry => MessagePackSerializer.Deserialize<object>(entry.Value))
                : [];

        private static bool? ReadBool(object value, string key) =>
            value is IDictionary<object, object> map && map.TryGetValue(key, out object? field) && field is bool flag ? flag : null;

        // First collection of a collectable grants CollectableComponent.RewardId once and updates box/explore/task progress.
        // Returns null when the object was already collected.
        internal static List<RewardGoods>? CollectSceneObject(Session session, int levelId, int placeId, int actorUuid)
        {
            BigWorldPlayerState state = session.player.BigWorldState;
            LevelSceneObjectTable row = SceneObjectsOf(levelId)[placeId];
            if (row.CollectRewardId <= 0 || state.ClaimedSceneObjects.Any(claim => claim.LevelId == levelId && claim.PlaceId == placeId))
                return null;
            state.ClaimedSceneObjects.Add(new BigWorldClaimedSceneObject
            {
                LevelId = levelId,
                PlaceId = placeId,
                Uuid = actorUuid,
                ClaimedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            });
            session.player.Save();

            List<RewardGoods> rewards = GrantReward(session, row.CollectRewardId);
            session.SendPush(new NotifyBigWorldBoxData { LevelId = levelId, BoxRewardedCnt = CountCollected(session.player, levelId) });
            if (row.CourseGroupId > 0)
                BigWorldArchiveModule.ReportExplorePoi(session, row.CourseGroupId, CollectedByCourseGroup(session.player, row.CourseGroupId));
            return rewards;
        }

        // Teleporter components are activated by interaction (TeleporterComponent.ActivateMode) and persisted per level.
        internal static bool ActivateTeleporter(Session session, int levelId, int placeId)
        {
            BigWorldPlayerState state = session.player.BigWorldState;
            if (!state.TeleporterData.TryGetValue(levelId, out List<int>? placeIds))
                state.TeleporterData[levelId] = placeIds = [];
            if (placeIds.Contains(placeId))
                return false;
            placeIds.Add(placeId);
            session.player.Save();
            session.SendPush(new NotifyBigWorldActivateTeleporter { LevelId = levelId, PlaceId = placeId });
            return true;
        }

        #endregion

        #region Player settings

        [RequestPacketHandler("BigWorldSaveFovDataRequest")]
        public static void BigWorldSaveFovDataRequestHandler(Session session, Packet.Request packet)
        {
            BigWorldSaveFovDataRequest request = packet.Deserialize<BigWorldSaveFovDataRequest>();
            // XBigWorldModel.lua:135: group 0 is the default perspective group; other groups come from BigWorldLevelFovSave.
            bool validGroup = request.FovGroupId == 0
                || TableReaderV2.Parse<BigWorldLevelFovSaveTable>().Any(row => row.FovGroupId == request.FovGroupId);
            // AscNet policy: perspectives are 1 and 2 (BigWorldConfig.DefaultPerspective is one of them).
            if (!validGroup || request.FovType is not (1 or 2))
            {
                session.SendResponse(new BigWorldSaveFovDataResponse { Code = CodeInvalidRequest }, packet.Id);
                return;
            }
            BigWorldPlayerState state = session.player.BigWorldState;
            if (request.FovGroupId == 0)
                state.FovType = request.FovType;
            else
                state.LevelFovDatas[request.FovGroupId] = request.FovType;
            session.player.Save();
            session.SendResponse(new BigWorldSaveFovDataResponse(), packet.Id);
        }

        [RequestPacketHandler("BigWorldGuideOpenRequest")]
        public static void BigWorldGuideOpenRequestHandler(Session session, Packet.Request packet)
        {
            BigWorldGuideOpenRequest request = packet.Deserialize<BigWorldGuideOpenRequest>();
            BigWorldOpenGuideTable? guide = TableReaderV2.Parse<BigWorldOpenGuideTable>().FirstOrDefault(row => row.Id == request.GuideId);
            BigWorldPlayerState state = session.player.BigWorldState;
            int code = guide is null ? CodeGuideIdError
                : session.BigWorldWorldId == 0 ? CodeNotInWorld
                : !TableReaderV2.Parse<BigWorldSysModuleTable>().Any(row => row.SysModuleId == guide.SysModuleId && row.WorldId == session.BigWorldWorldId) ? CodeSysModuleIdError
                : state.GuideData.Contains(request.GuideId) ? CodeGuideAlreadyCompleted
                : 0;
            if (code == 0)
            {
                state.GuideData.Add(request.GuideId);
                session.player.Save();
            }
            session.SendResponse(new BigWorldGuideOpenResponse { Code = code }, packet.Id);
        }

        [RequestPacketHandler("BigWorldMarkCustomParamRequest")]
        public static void BigWorldMarkCustomParamRequestHandler(Session session, Packet.Request packet)
        {
            BigWorldMarkCustomParamRequest request = packet.Deserialize<BigWorldMarkCustomParamRequest>();
            if (!TableReaderV2.Parse<BigWorldCustomParamTable>().Any(row => row.Param == request.Id))
            {
                session.SendResponse(new BigWorldMarkCustomParamResponse { Code = CodeInvalidRequest }, packet.Id);
                return;
            }
            List<int> marks = session.player.BigWorldState.CustomParamMarkData;
            bool changed = request.IsUnmark ? marks.Remove(request.Id) : !marks.Contains(request.Id);
            if (!request.IsUnmark && changed)
                marks.Add(request.Id);
            if (changed)
            {
                session.player.Save();
                BigWorldTaskModule.OnProgressChanged(session);
            }
            session.SendResponse(new BigWorldMarkCustomParamResponse(), packet.Id);
        }

        [RequestPacketHandler("BigWorldCheckIsShowMainRedPointRequest")]
        public static void BigWorldCheckIsShowMainRedPointRequestHandler(Session session, Packet.Request packet)
        {
            BigWorldCheckIsShowMainRedPointRequest request = packet.Deserialize<BigWorldCheckIsShowMainRedPointRequest>();
            if (!TableReaderV2.Parse<BigWorldSysModuleTable>().Any(row => row.SysModuleId == request.SysModuleId))
            {
                session.SendResponse(new BigWorldCheckIsShowMainRedPointResponse { Code = CodeSysModuleIdError }, packet.Id);
                return;
            }
            // Retail pushes the red points before the response.
            session.SendPush(BuildMainRedPoint(session.player));
            session.SendResponse(new BigWorldCheckIsShowMainRedPointResponse(), packet.Id);
        }

        [RequestPacketHandler("BigWorldSetTrackMapPinIdRequest")]
        public static void BigWorldSetTrackMapPinIdRequestHandler(Session session, Packet.Request packet)
        {
            BigWorldSetTrackMapPinIdRequest request = packet.Deserialize<BigWorldSetTrackMapPinIdRequest>();
            BigWorldMapTrackPinData? pin = request.MapTrackPinData;
            bool valid = pin is not null && request.Opt is >= TrackOperatorBegin and <= TrackOperatorFinish
                && IsWorldLevel(pin.WorldId, pin.LevelId)
                && (request.Opt == TrackOperatorBegin ? pin.TrackPinId > 0 : pin.TrackPinId >= 0);
            if (!valid)
            {
                session.SendResponse(new BigWorldSetTrackMapPinIdResponse { Code = CodeInvalidRequest }, packet.Id);
                return;
            }
            session.player.BigWorldState.MapTrackPin = request.Opt == TrackOperatorBegin
                ? new BigWorldMapTrackPin { WorldId = pin!.WorldId, LevelId = pin.LevelId, TrackPinId = pin.TrackPinId }
                : null;
            session.player.Save();
            session.SendResponse(new BigWorldSetTrackMapPinIdResponse(), packet.Id);
        }

        #endregion

        #region Payload builders

        private static BigWorldPlayerData BuildPlayerData(Player player)
        {
            BigWorldPlayerState state = player.BigWorldState;
            BigWorldPlayerData data = new()
            {
                LastPosition = ToDlcVector(state.LastPosition),
                LastRotation = GetRotationY(state) is double rotationY ? new Theatre5DlcVector3 { Y = (float)rotationY } : null,
                LastLevelId = state.LastLevelId,
                LastWorldId = state.LastWorldId,
                CurNpcPos = state.CurNpcPos,
                TeleporterData = state.TeleporterData.ToDictionary(entry => entry.Key, entry => entry.Value.ToList()),
                LevelPlayDatas = state.LevelPlayDatas.ToDictionary(entry => entry.Key, entry => new BigWorldLevelPlayDataDto { IsFullCleared = entry.Value.IsFullCleared }),
                EnteredLevelIds = state.EnteredLevelIds.ToList(),
                BigWorldGuideData = state.GuideData.ToList(),
                CustomParamMarkData = state.CustomParamMarkData.ToList(),
                // XBigWorldModel.InitPerspective logs an error for FovType 0: unset falls back to BigWorldConfig.DefaultPerspective.
                FovData = new BigWorldFovData
                {
                    FovType = state.FovType > 0 ? state.FovType : ConfigInt("DefaultPerspective"),
                    LevelFovDatas = new Dictionary<int, int>(state.LevelFovDatas)
                },
                MapTrackPinData = state.MapTrackPin is { } pin
                    ? new BigWorldMapTrackPinData { WorldId = pin.WorldId, LevelId = pin.LevelId, TrackPinId = pin.TrackPinId }
                    : null
            };
            BigWorldCharacterModule.FillPlayerData(player, data);
            BigWorldArchiveModule.FillPlayerData(player, data);
            BigWorldQuestModule.FillPlayerData(player, data);
            return data;
        }

        // Box counts for every level of the world that has collectables (XBigWorldMapModel.UpdateAllCollectionsCount).
        internal static NotifyBigWorldMapData BuildMapData(Player player, int worldId) => new()
        {
            BoxRewardedCntData = LevelSceneObjects.Value
                .Where(entry => IsWorldLevel(worldId, entry.Key) && entry.Value.Values.Any(row => row.CollectRewardId > 0))
                .OrderBy(entry => entry.Key)
                .ToDictionary(entry => entry.Key, entry => CountCollected(player, entry.Key))
        };

        // AscNet policy: retail's entrance red-point rule is server-side and unobserved; a SysModule's world shows the
        // red point until the player has entered it (XBigWorldGamePlayModel.CheckSkyGardenEntranceRedPoint).
        internal static NotifyBigWorldMainRedPoint BuildMainRedPoint(Player player) => new()
        {
            RedPoints = TableReaderV2.Parse<BigWorldSysModuleTable>()
                .GroupBy(row => row.SysModuleId)
                .ToDictionary(group => group.Key, group => group.Any(row => !player.BigWorldState.EnteredWorldIds.Contains(row.WorldId)))
        };

        internal static NotifyExternalRequiredBigWorldPlayerData BuildExternalRequiredPlayerData(Player player) => new()
        {
            EnteredBigWorldIds = player.BigWorldState.EnteredWorldIds.ToList(),
            Gender = BigWorldCharacterModule.GetGender(player),
            CommanderFashionBags = BigWorldCharacterModule.GetCommanderFashionBags(player)
        };

        private static BigWorldVector3? ToVector(Theatre5DlcVector3? value) =>
            value is null ? null : new BigWorldVector3 { X = value.X, Y = value.Y, Z = value.Z };

        private static Theatre5DlcVector3? ToDlcVector(BigWorldVector3? value) =>
            value is null ? null : new Theatre5DlcVector3 { X = (float)value.X, Y = (float)value.Y, Z = (float)value.Z };

        internal static double? GetRotationY(BigWorldPlayerState state)
        {
            if (state.LastRotationY is double rotationY)
                return rotationY;
            if (state.LastRotation is { } q)
                return QuaternionYaw(q.X, q.Y, q.Z, q.W);
            return null;
        }

        internal static double QuaternionYaw(double x, double y, double z, double w) =>
            NormalizeYaw(Math.Atan2(2D * (w * y + x * z), 1D - 2D * (y * y + z * z)) * 180D / Math.PI);

        internal static double NormalizeYaw(double yaw)
        {
            yaw %= 360D;
            return yaw < 0D ? yaw + 360D : yaw;
        }

        #endregion
    }
}
