using AscNet.Common;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Handlers.Drops;
using AscNet.Table.V2.share.character.skill;
using AscNet.Table.V2.share.fuben;
using AscNet.Table.V2.share.fuben.mainline2;
using AscNet.Table.V2.share.item;
using AscNet.Table.V2.share.reward;
using AscNet.Table.V2.share.robot;
using MessagePack;

namespace AscNet.GameServer.Handlers
{
    #region MsgPackScheme
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    public enum RewardType
    {
        Item = 1,
        Character = 2,
        Equip = 3,
        Fashion = 4,
        BaseEquip = 5,
        Furniture = 9,
        HeadPortrait = 10,
        DormCharacter = 11,
        ChatEmoji = 12,
        WeaponFashion = 13,
        Collection = 14,
        Background = 15,
        Pokemon = 16,
        Partner = 17,
        Nameplate = 18,
        RankScore = 20,
        Medal = 21,
        DrawTicket = 22
    }

    [MessagePackObject(true)]
    public class Operation
    {
        public bool? MoveOperated { get; set; }
        public int MoveOperation { get; set; }
        public int CameraRotationX { get; set; }
        public int CameraRotationY { get; set; }
        public int CameraInput { get; set; }
        public long IncId { get; set; }
        public int[] ClickOperation { get; set; }
        public int[] SpecialOperation { get; set; }
    }

    [MessagePackObject(true)]
    public class NpcHp
    {
        public int CharacterId { get; set; }
        public int NpcId { get; set; }
        public int Type { get; set; }
        public int Level { get; set; }
        public List<int> BuffIds { get; set; }
        public Dictionary<int, dynamic> AttrTable { get; set; }
    }

    [MessagePackObject(true)]
    public partial class NpcDpsTable
    {
        public int Value { get; set; }
        public int MaxValue { get; set; }
        public int RoleId { get; set; }
        public int NpcId { get; set; }
        public int CharacterId { get; set; }
        public int DamageTotal { get; set; }
        public int DamageNormal { get; set; }
        public List<int> DamageMagic { get; set; } = new();
        public int BreakEndure { get; set; }
        public int Cure { get; set; }
        public int Hurt { get; set; }
        public int Type { get; set; }
        public int Level { get; set; }
        public List<int> BuffIds { get; set; } = new();
        public dynamic AttrTable { get; set; }
    }

    [MessagePackObject(true)]
    public class FightSettleResult
    {
        public bool IsWin { get; set; }
        public bool IsForceExit { get; set; }
        public uint StageId { get; set; }
        public int StageLevel { get; set; }
        public long FightId { get; set; }
        public int RebootCount { get; set; }
        public int AddStars { get; set; }
        public int Achievement { get; set; }
        public long StartFrame { get; set; }
        public long SettleFrame { get; set; }
        public long PauseFrame { get; set; }
        public long ExSkillPauseFrame { get; set; }
        public long SettleCode { get; set; }
        public int DodgeTimes { get; set; }
        public int NormalAttackTimes { get; set; }
        public int ConsumeBallTimes { get; set; }
        public int StuntSkillTimes { get; set; }
        public int PauseTimes { get; set; }
        public int HighestCombo { get; set; }
        public int DamagedTimes { get; set; }
        public int MatrixTimes { get; set; }
        public long HighestDamage { get; set; }
        public long TotalDamage { get; set; }
        public long TotalDamaged { get; set; }
        public long TotalCure { get; set; }
        public long[] PlayerIds { get; set; }
        public dynamic[] PlayerData { get; set; }
        public dynamic? IntToIntRecord { get; set; }
        public dynamic? StringToIntRecord { get; set; }
        public Dictionary<long, Operation> Operations { get; set; }
        public long[] Codes { get; set; }
        public long LeftTime { get; set; }
        public Dictionary<int, NpcHp> NpcHpInfo { get; set; }
        public Dictionary<int, NpcDpsTable> NpcDpsTable { get; set; }
        public dynamic[] EventSet { get; set; }
        public long DeathTotalMyTeam { get; set; }
        public long DeathTotalEnemy { get; set; }
        public Dictionary<int, int> DeathRecord { get; set; } = new();
        public dynamic[] GroupDropDatas { get; set; }
        public dynamic? EpisodeFightResults { get; set; }
        public dynamic? CustomData { get; set; }
    }

    [MessagePackObject(true)]
    public class FightSettleRequest
    {
        public FightSettleResult Result { get; set; }
    }

    [MessagePackObject(true)]
    public class EnterStoryRequest
    {
        public int StageId { get; set; }
    }

    [MessagePackObject(true)]
    public class EnterStoryResponse
    {
        public int Code { get; set; }
    }

    [MessagePackObject(true)]
    public class FightRebootRequest
    {
        public int FightId { get; set; }
        public int RebootCount { get; set; }
    }

    [MessagePackObject(true)]
    public class FightRebootResponse
    {
        public int Code { get; set; }
    }

    [MessagePackObject(true)]
    public class KcpConfirmRequest
    {
    }

    [MessagePackObject(true)]
    public class KcpConfirmResponse
    {
    }

    [MessagePackObject(true)]
    public class JoinFightRequest
    {
        public long FightId { get; set; }
        public int PlayerId { get; set; }
        public string Token { get; set; }
    }

    [MessagePackObject(true)]
    public class JoinFightResponse
    {
        public int Code { get; set; }
        public int Port { get; set; }
        public uint Conv { get; set; }
        public object? FightData { get; set; }
    }

    [MessagePackObject(true)]
    public class FightHeartbeatRequest
    {
        public int Frame { get; set; }
    }

    [MessagePackObject(true)]
    public class FightHeartbeatResponse
    {
        public int Code { get; set; }
    }

    [MessagePackObject(true)]
    public class FightReconnectRequest
    {
        public long FightId { get; set; }
        public int PlayerId { get; set; }
        public int Frame { get; set; }
    }

    [MessagePackObject(true)]
    public class FightReconnectResponse
    {
        public int Code { get; set; }
        public int Port { get; set; }
        public uint Conv { get; set; }
        public List<dynamic> Operations { get; set; } = new();
    }

    [MessagePackObject(true)]
    public class LoadCompleteRequest
    {
    }

    [MessagePackObject(true)]
    public class LoadCompleteResponse
    {
        public int Code { get; set; }
    }

    [MessagePackObject(true)]
    public class CheckCodeRequest
    {
        public int Frame { get; set; }
        public int Code { get; set; }
    }

    [MessagePackObject(true)]
    public class CheckCodeResponse
    {
        public int Code { get; set; }
    }

    [MessagePackObject(true)]
    public class LeaveFightRequest
    {
    }

    [MessagePackObject(true)]
    public class LeaveFightResponse
    {
        public int Code { get; set; }
    }
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    #endregion

    internal class FightModule
    {
        private static readonly Lazy<HashSet<uint>> MainLine2AchievementStageIds = new(() => TableReaderV2.Parse<MainLine2StageTable>()
            .Select(stage => (uint)stage.Id)
            .ToHashSet());

        [RequestPacketHandler("KcpConfirmRequest")]
        public static void KcpConfirmRequestHandler(Session session, Packet.Request packet)
        {
            session.SendResponse(new KcpConfirmResponse(), packet.Id);
        }

        [RequestPacketHandler("JoinFightRequest")]
        public static void JoinFightRequestHandler(Session session, Packet.Request packet)
        {
            _ = MessagePackSerializer.Deserialize<JoinFightRequest>(packet.Content);
            session.SendResponse(new JoinFightResponse
            {
                Code = 1023
            }, packet.Id);
        }

        [RequestPacketHandler("FightHeartbeatRequest")]
        public static void FightHeartbeatRequestHandler(Session session, Packet.Request packet)
        {
            _ = MessagePackSerializer.Deserialize<FightHeartbeatRequest>(packet.Content);
            session.SendResponse(new FightHeartbeatResponse(), packet.Id);
        }

        [RequestPacketHandler("FightReconnectRequest")]
        public static void FightReconnectRequestHandler(Session session, Packet.Request packet)
        {
            _ = MessagePackSerializer.Deserialize<FightReconnectRequest>(packet.Content);
            session.SendResponse(new FightReconnectResponse
            {
                Code = 1033
            }, packet.Id);
        }

        [RequestPacketHandler("LoadCompleteRequest")]
        public static void LoadCompleteRequestHandler(Session session, Packet.Request packet)
        {
            DlcModule.SendPendingBigWorldStartFightNotify(session);
            session.SendResponse(new LoadCompleteResponse(), packet.Id);
            DlcModule.SendPendingBigWorldLoadCompleteXRpc(session);
        }

        [RequestPacketHandler("CheckCodeRequest")]
        public static void CheckCodeRequestHandler(Session session, Packet.Request packet)
        {
            _ = MessagePackSerializer.Deserialize<CheckCodeRequest>(packet.Content);
            session.SendResponse(new CheckCodeResponse(), packet.Id);
        }

        [RequestPacketHandler("LeaveFightRequest")]
        public static void LeaveFightRequestHandler(Session session, Packet.Request packet)
        {
            session.fight = null;
            session.SendResponse(new LeaveFightResponse(), packet.Id);
        }

        [RequestPacketHandler("PreFightRequest")]
        public static void PreFightRequestHandler(Session session, Packet.Request packet)
        {
            PreFightRequest req = MessagePackSerializer.Deserialize<PreFightRequest>(packet.Content);

            StageTable? stageTable = ResolveStageTable(req.PreFightData.StageId, out bool isCurrentStudyStage);
            if (stageTable is null && !MainLineLuosaitaPayloadFactory.HasCapturedStageProgress((int)req.PreFightData.StageId))
            {
                string cardIds = req.PreFightData.CardIds is null
                    ? "<null>"
                    : string.Join(",", req.PreFightData.CardIds);
                session.log.Warn($"[STAGE-PROBE] PreFightStageTableMissing stageId={req.PreFightData.StageId} challengeCount={req.PreFightData.ChallengeCount} cardIds={cardIds}");
            }

            StageLevelControlTable? levelControl = ResolveStageLevelControl(
                req.PreFightData.StageId,
                (int)session.player.PlayerData.Level);

            PreFightResponse rsp = new()
            {
                Code = 0,
                FightData = new()
                {
                    Online = false,
                    FightId = req.PreFightData.StageId + (uint)Random.Shared.NextInt64(0, uint.MaxValue - req.PreFightData.StageId),
                    OnlineMode = 0,
                    Seed = (uint)Random.Shared.NextInt64(0, uint.MaxValue),
                    StageId = req.PreFightData.StageId,
                    RebootId = stageTable?.RebootId ?? 0,
                    PassTimeLimit = stageTable?.PassTimeLimit ?? 0,
                    StarsMark = 0,
                    MonsterLevel = levelControl?.MonsterLevel ?? new()
                }
            };

            rsp.FightData.RoleData.Add(new()
            {
                Id = (uint)session.player.PlayerData.Id,
                Camp = 1,
                Name = session.player.PlayerData.Name,
                IsRobot = false,
                NpcData = new()
            });

            IReadOnlyList<uint> cardIdsToDeploy = (req.PreFightData.CardIds ?? [])
                .Where(cardId => cardId > 0)
                .ToList();
            List<int> requestedRobotIds = req.PreFightData.RobotIds?
                .Where(robotId => robotId > 0)
                .ToList() ?? [];
            List<int> robotIds;
            if (CurrentClientStudyTables.TryGetConfiguredRobotIds(req.PreFightData.StageId, out IReadOnlyList<int> configuredStudyRobotIds))
            {
                List<int> validRequestedRobotIds = requestedRobotIds
                    .Where(configuredStudyRobotIds.Contains)
                    .ToList();
                robotIds = validRequestedRobotIds.Count > 0
                    ? validRequestedRobotIds
                    : configuredStudyRobotIds.ToList();
            }
            else
            {
                robotIds = stageTable?.RobotId ?? requestedRobotIds;
            }

            bool isTheatreFight = BiancaTheatreModule.TryGetTheatreFightDeployment(
                session,
                req.PreFightData.StageId,
                req.PreFightData.CardIds,
                req.PreFightData.RobotIds,
                out IReadOnlyList<uint> theatreCardIds,
                out IReadOnlyList<int> theatreRobotIds);
            if (isTheatreFight)
            {
                cardIdsToDeploy = theatreCardIds;
                robotIds = theatreRobotIds.ToList();
            }

            Dictionary<int, RobotTable> robotRowsToDeploy = new();
            if (robotIds.Count > 0)
            {
                List<int> deployableRobotIds = new(robotIds.Count);
                foreach (int robotId in robotIds)
                {
                    RobotTable? robot = ResolveRobotTable(robotId, isCurrentStudyStage);
                    if (robot?.CharacterId is not > 0)
                        continue;

                    deployableRobotIds.Add(robotId);
                    robotRowsToDeploy.TryAdd(robotId, robot);
                }
                robotIds = deployableRobotIds;
            }

            if (robotIds.Count == 0 && cardIdsToDeploy.Count == 0)
            {
                int currentTeamId = (int)session.player.PlayerData.CurrTeamId;
                if (session.player.TeamGroups.TryGetValue(currentTeamId, out TeamGroupDatum? teamGroup))
                {
                    cardIdsToDeploy = teamGroup.TeamData
                        .OrderBy(member => member.Key)
                        .Select(member => (uint)member.Value)
                        .Where(cardId => cardId > 0)
                        .ToList();
                }
            }

            for (int i = 0; i < cardIdsToDeploy.Count; i++)
            {
                uint cardId = cardIdsToDeploy[i];
                CharacterData? characterData = session.character.Characters.FirstOrDefault(x => x.Id == cardId);
                IEnumerable<EquipData> equips;
                if (characterData is null)
                {
                    if (!BiancaTheatreModule.TryBuildTheatreCharacterData(session, cardId, out CharacterData transientCharacter, out IReadOnlyList<EquipData> transientEquips))
                        continue;

                    characterData = transientCharacter;
                    equips = transientEquips;
                }
                else
                {
                    equips = session.character.Equips.Where(x => x.CharacterId == cardId);
                }

                rsp.FightData.RoleData.First(x => x.Id == session.player.PlayerData.Id).NpcData.Add(i, new
                {
                    Character = characterData,
                    Equips = equips
                });
            }

            if (robotIds.Count > 0
                && (stageTable is null || req.PreFightData.CardIds is null || cardIdsToDeploy.Count == 0 || (cardIdsToDeploy.Count + robotIds.Count) == 3))
            {
                int npcKey = rsp.FightData.RoleData.First(x => x.Id == session.player.PlayerData.Id).NpcData.Keys.Count;
                foreach (var robotId in robotIds)
                {
                    RobotTable? robot = robotRowsToDeploy.GetValueOrDefault(robotId);
                    if (robot is null)
                    {
                        session.log.Warn($"[STAGE-PROBE] PreFightRobotTableMissing stageId={req.PreFightData.StageId} robotId={robotId}");
                        continue;
                    }

                    CharacterSkillTable? characterSkill = TableReaderV2.Parse<CharacterSkillTable>().Find(x => x.CharacterId == robot.CharacterId);
                    IEnumerable<int> skills = characterSkill?.SkillGroupId.SelectMany(x => TableReaderV2.Parse<CharacterSkillGroupTable>().Find(y => y.Id == x)?.SkillId ?? new List<int>()) ?? new List<int>();
                    AscNet.Table.V2.share.character.CharacterTable? robotCharacter = TableReaderV2.Parse<AscNet.Table.V2.share.character.CharacterTable>()
                        .Find(character => character.Id == robot.CharacterId);
                    uint fashionId = (uint)(robotCharacter?.DefaultNpcFashtionId > 0
                        ? robotCharacter.DefaultNpcFashtionId
                        : robot.FashionId);
                    List<EquipData> equips = new()
                    {
                        new()
                        {
                            TemplateId = (uint)Convert.ToInt32(robot.WeaponId),
                            Level = Convert.ToInt32(robot.WeaponLevel),
                            Breakthrough = Convert.ToInt32(robot.WeaponBeakThrough),
                        }
                    };

                    int waferCount = Math.Min(robot.WaferId.Count, Math.Min(robot.WaferLevel.Count, robot.WaferBreakThrough.Count));
                    for (int i = 0; i < waferCount; i++)
                    {
                        equips.Add(new()
                        {
                            TemplateId = (uint)Convert.ToInt32(robot.WaferId[i]),
                            Level = Convert.ToInt32(robot.WaferLevel[i]),
                            Breakthrough = Convert.ToInt32(robot.WaferBreakThrough[i])
                        });
                    }

                    rsp.FightData.RoleData.First(x => x.Id == session.player.PlayerData.Id).NpcData.Add(npcKey, new
                    {
                        Character = new CharacterData()
                        {
                            Id = (uint)Convert.ToInt32(robot.CharacterId),
                            Level = Convert.ToInt32(robot.CharacterLevel),
                            Exp = 0,
                            Quality = Convert.ToInt32(robot.CharacterQuality),
                            InitQuality = Convert.ToInt32(robot.CharacterQuality),
                            Star = Convert.ToInt32(robot.CharacterStar),
                            Grade = Convert.ToInt32(robot.CharacterGrade),
                            SkillList = skills.Where(x => !robot.RemoveSkillId.Contains(x)).Select(x => new CharacterSkill() { Id = (uint)x, Level = Math.Min(Convert.ToInt32(robot.SkillLevel), TableReaderV2.Parse<CharacterSkillLevelEffectTable>().OrderByDescending(x => x.Level).FirstOrDefault(y => y.SkillId == x)?.Level ?? 1) }).ToList(),
                            FashionId = fashionId,
                            CreateTime = 0,
                            TrustLv = 1,
                            TrustExp = 0,
                            Ability = robot.ShowAbility ?? 0,
                            LiberateLv = robot.LiberateLv ?? 0,
                            CharacterHeadInfo = new()
                            {
                                HeadFashionId = fashionId
                            }
                        },
                        Equips = equips,
                        IsRobot = true,
                        RobotId = robotId,
                        IsNpc = false,
                    });
                    npcKey++;
                }
            }

            if (isTheatreFight)
                BiancaTheatreModule.ApplyTheatreFightStageData(session, req.PreFightData.StageId, rsp);

            session.fight = new(req);
            session.SendResponse(rsp, packet.Id);
        }

        [RequestPacketHandler("FightRebootRequest")]
        public static void HandleFightRebootRequestHandler(Session session, Packet.Request packet)
        {
            FightRebootRequest req = MessagePackSerializer.Deserialize<FightRebootRequest>(packet.Content);
            session.SendResponse(new FightRebootResponse(), packet.Id);
        }

        [RequestPacketHandler("EnterStoryRequest")]
        public static void HandleEnterStoryRequestHandler(Session session, Packet.Request packet)
        {
            EnterStoryRequest req = packet.Deserialize<EnterStoryRequest>();

            StageDatum stageData = new()
            {
                StageId = req.StageId,
                StarsMark = 7,
                Passed = true,
                PassTimesToday = 0,
                PassTimesTotal = 1,
                BuyCount = 0,
                Score = 0,
                LastPassTime = (uint)DateTimeOffset.Now.ToUnixTimeSeconds(),
                RefreshTime = (uint)DateTimeOffset.Now.ToUnixTimeSeconds(),
                CreateTime = (uint)DateTimeOffset.Now.ToUnixTimeSeconds(),
                BestRecordTime = 0,
                LastRecordTime = 0
            };
            session.stage.AddStage(stageData);

            session.SendPush(new NotifyStageData() { StageList = [stageData] });
            SendMainLineLuosaitaSectionInfoIfCaptured(session, req.StageId);
            session.SendResponse(new EnterStoryResponse(), packet.Id);
        }

        [RequestPacketHandler("TeamSetTeamRequest")]
        public static void HandleTeamSetTeamRequestHandler(Session session, Packet.Request packet)
        {
            TeamSetTeamRequest req = MessagePackSerializer.Deserialize<TeamSetTeamRequest>(packet.Content);

            session.player.TeamGroups[(int)session.player.PlayerData.CurrTeamId] = new()
            {
                CaptainPos = req.TeamData.CaptainPos,
                FirstFightPos = req.TeamData.FirstFightPos,
                TeamId = req.TeamData.TeamId,
                TeamType = 1,
                TeamName = req.TeamData.TeamName,
                TeamData = req.TeamData.TeamData
            };

            session.SendResponse(new TeamSetTeamResponse(), packet.Id);
        }

        [RequestPacketHandler("EnterChallengeRequest")]
        public static void HandleEnterChallengeRequestHandler(Session session, Packet.Request packet)
        {
            session.SendResponse(new EnterChallengeResponse(), packet.Id);
        }

        [RequestPacketHandler("FightSettleRequest")]
        public static void FightSettleRequestHandler(Session session, Packet.Request packet)
        {
            FightSettleRequest req = MessagePackSerializer.Deserialize<FightSettleRequest>(packet.Content);
            StageTable? stageTable = ResolveStageTable(req.Result.StageId, out _);
            if (stageTable is null && !MainLineLuosaitaPayloadFactory.HasCapturedStageProgress((int)req.Result.StageId))
            {
                session.log.Warn($"[STAGE-PROBE] FightSettleStageTableMissing stageId={req.Result.StageId} fightId={req.Result.FightId}");
            }

            StageLevelControlTable? levelControl = ResolveStageLevelControl(
                req.Result.StageId,
                (int)session.player.PlayerData.Level);
            int challengeCount = session.fight?.PreFight.PreFightData.ChallengeCount ?? 1;
            uint responseStageId = ResolveFightSettleStageId(session, req);
            bool isQuickClear = responseStageId != req.Result.StageId;
            StageDatum? previousStageData = session.stage.Stages.TryGetValue(responseStageId, out StageDatum? existingStageData) ? existingStageData : null;
            bool isFirstClear = previousStageData is null;
            bool isSuccessfulSettle = req.Result.IsWin && !req.Result.IsForceExit;
            if (!isSuccessfulSettle)
            {
                BiancaTheatreModule.TrySendTheatreRetreatSettle(session, req.Result.StageId);
                session.fight = null;
                session.SendResponse(BuildFailedFightSettleResponse(responseStageId, req), packet.Id);
                return;
            }
            int teamExp = stageTable is null ? 0 : GetStageTeamExp(stageTable, isFirstClear) * challengeCount;
            int cardExp = stageTable is null ? 0 : GetStageCardExp(stageTable, isFirstClear) * challengeCount;

            List<int> rewardIds = new();
            void AddRewardId(int? rewardId)
            {
                if (rewardId is > 0 && !rewardIds.Contains(rewardId.Value))
                    rewardIds.Add(rewardId.Value);
            }

            if (stageTable is not null)
            {
                if (isFirstClear)
                {
                    AddRewardId(stageTable.FinishDropId);
                    AddRewardId(stageTable.FirstRewardId);
                    AddRewardId(levelControl?.FinishDropId);
                    AddRewardId(levelControl?.FirstRewardId);
                }
                else
                {
                    AddRewardId(stageTable.FinishDropId);
                    AddRewardId(levelControl?.FinishDropId);
                }
            }

            List<List<RewardGoods>> multiRewards = new();
            List<RewardTable> rewardTables = TableReaderV2.Parse<RewardTable>()
                .Where(x => rewardIds.Contains(x.Id))
                .ToList();
            if (stageTable is not null && rewardTables.Count == 0)
            {
                rewardIds.Clear();
                if (isFirstClear)
                {
                    AddRewardId(stageTable.FinishRewardShow);
                    AddRewardId(stageTable.FirstRewardShow);
                    AddRewardId(levelControl?.FinishRewardShow);
                    AddRewardId(levelControl?.FirstRewardShow);
                }
                else
                {
                    AddRewardId(stageTable.FinishRewardShow);
                    AddRewardId(levelControl?.FinishRewardShow);
                }

                rewardTables.AddRange(TableReaderV2.Parse<RewardTable>().Where(x => rewardIds.Contains(x.Id)));
            }

            NotifyItemDataList notifyItemData = new();
            if (teamExp > 0)
            {
                notifyItemData.ItemDataList.Add(session.inventory.Do(Inventory.TeamExp, teamExp));
            }

            for (int i = 0; i < challengeCount; i++)
            {
                var rewardGoods = rewardTables
                    .SelectMany(x => x.SubIds)
                    .Select(x => TableReaderV2.Parse<RewardGoodsTable>().FirstOrDefault(y => y.Id == x))
                    .OfType<RewardGoodsTable>();

                var rewards = RewardHandler.GiveRewards(rewardGoods, session);
                multiRewards.Add(new List<RewardGoods>(rewards));
            }

            if (notifyItemData.ItemDataList.Count > 0)
            {
                session.SendPush(notifyItemData);
            }
            session.ExpSanityCheck();

            if (cardExp > 0)
            {
                Dictionary<int, long> team = session.player.TeamGroups[(int)session.player.PlayerData.CurrTeamId].TeamData;
                NotifyCharacterDataList charData = new();
                
                foreach (KeyValuePair<int, long> member in team)
                {
                    if (member.Value > 0)
                    {
                        var character = session.character.AddCharacterExp((int)member.Value, cardExp, (int)session.player.PlayerData.Level);
                        if (character is not null)
                            charData.CharacterDataList.Add(character);
                    }
                }
                
                session.SendPush(charData);
                session.character.Save();
            }

            List<long> bestCardIds = req.Result.NpcDpsTable?
                .Where(x => x.Value.CharacterId > 0)
                .Select(x => (long)x.Value.CharacterId)
                .ToList() ?? [];
            int requestedAchievement = IsMainLine2AchievementStage(req.Result.StageId) || IsMainLine2AchievementStage(responseStageId)
                ? Math.Max(0, req.Result.Achievement)
                : 0;
            long stageStarsMark = (previousStageData?.StarsMark ?? 0L) | (isQuickClear ? 0L : 7L);
            long stageAchievement = (previousStageData?.Achievement ?? 0L) | (long)requestedAchievement;
            StageDatum stageData = BuildFightSettleStageDatum(
                responseStageId,
                stageStarsMark,
                stageAchievement,
                bestCardIds,
                isQuickClear,
                previousStageData);
            session.stage.AddStage(stageData);

            if (isQuickClear && MainLineLuosaitaPayloadFactory.HasCapturedStageProgress((int)req.Result.StageId))
            {
                StageDatum? previousLuosaitaProgressStageData = session.stage.Stages.TryGetValue(req.Result.StageId, out StageDatum? existingLuosaitaProgressStageData) ? existingLuosaitaProgressStageData : null;
                StageDatum luosaitaProgressStageData = BuildFightSettleStageDatum(
                    req.Result.StageId,
                    (previousLuosaitaProgressStageData?.StarsMark ?? 0L) | 7L,
                    (previousLuosaitaProgressStageData?.Achievement ?? 0L) | (long)requestedAchievement,
                    bestCardIds,
                    false,
                    previousLuosaitaProgressStageData);
                session.stage.AddStage(luosaitaProgressStageData);
            }

            session.player.Save();
            session.inventory.Save();
            session.character.Save();
            session.stage.Save();

            FightSettleResponse fightSettleResponse = new()
            {
                Code = 0,
                Settle = new()
                {
                    IsWin = req.Result.IsWin,
                    StageId = stageData.StageId,
                    StarsMark = (int)stageData.StarsMark,
                    Achievement = requestedAchievement,
                    RewardGoodsList = multiRewards.Count > 0 ? multiRewards.First() : [],
                    LeftTime = (int)req.Result.LeftTime,
                    NpcHpInfo = req.Result.NpcHpInfo,
                    MultiRewardGoodsList = multiRewards,
                    ChallengeCount = isQuickClear ? 0 : challengeCount
                }
            };

            session.fight = null;
            session.SendPush(new NotifyStageData() { StageList = new() { stageData } });
            StudyProgressModule.SendTeachingStageUpdate(session, stageData);
            bool sentTheatreProgress = BiancaTheatreModule.TrySendTheatreFightClearProgress(session, req.Result.StageId);
            if (!sentTheatreProgress)
            {
                SendMainLineLuosaitaSectionInfoIfCaptured(session, (int)req.Result.StageId);
            }
            TaskModule.RecordStageClear(session, (int)req.Result.StageId, challengeCount);
            session.SendResponse(fightSettleResponse, packet.Id);
        }

        private static StageTable? ResolveStageTable(uint stageId, out bool isCurrentStudyStage)
        {
            isCurrentStudyStage = CurrentClientStudyTables.TryGetStage(stageId, out StageTable currentStage);
            return isCurrentStudyStage
                ? currentStage
                : TableReaderV2.Parse<StageTable>().FirstOrDefault(stage => stage.StageId == stageId);
        }

        private static StageLevelControlTable? ResolveStageLevelControl(uint stageId, int playerLevel)
        {
            IEnumerable<StageLevelControlTable> controls = CurrentClientStudyTables.TryGetStageLevelControls(stageId, out IReadOnlyList<StageLevelControlTable> currentControls)
                ? currentControls
                : TableReaderV2.Parse<StageLevelControlTable>().Where(control => control.StageId == stageId);
            return controls
                .OrderBy(control => Math.Abs(playerLevel - control.MaxLevel))
                .FirstOrDefault();
        }

        private static RobotTable? ResolveRobotTable(int robotId, bool isCurrentStudyStage)
        {
            if (isCurrentStudyStage)
                return CurrentClientStudyTables.TryGetRobot(robotId, out RobotTable currentRobot) ? currentRobot : null;

            return TableReaderV2.Parse<RobotTable>().FirstOrDefault(robot => robot.Id == robotId);
        }

        private static bool IsMainLine2AchievementStage(uint stageId)
        {
            return MainLine2AchievementStageIds.Value.Contains(stageId);
        }

        private static uint ResolveFightSettleStageId(Session session, FightSettleRequest req)
        {
            uint speedrunStageId = session.fight?.PreFight.PreFightData.SpeedrunStageId ?? 0;
            return speedrunStageId != 0 ? speedrunStageId : req.Result.StageId;
        }

        private static FightSettleResponse BuildFailedFightSettleResponse(uint stageId, FightSettleRequest req)
        {
            return new FightSettleResponse
            {
                Code = 0,
                Settle = new()
                {
                    IsWin = false,
                    StageId = stageId,
                    StarsMark = 0,
                    Achievement = 0,
                    RewardGoodsList = null!,
                    LeftTime = (int)req.Result.LeftTime,
                    NpcHpInfo = req.Result.NpcHpInfo,
                    MultiRewardGoodsList = null!,
                    ChallengeCount = 0
                }
            };
        }

        private static StageDatum BuildFightSettleStageDatum(
            uint stageId,
            long starsMark,
            long achievement,
            List<long> bestCardIds,
            bool isQuickClear,
            StageDatum? previousStageData)
        {
            long now = DateTimeOffset.Now.ToUnixTimeSeconds();
            return new StageDatum
            {
                StageId = stageId,
                StarsMark = starsMark,
                Achievement = achievement,
                Passed = true,
                PassTimesToday = previousStageData?.PassTimesToday ?? 0,
                PassTimesTotal = (previousStageData?.PassTimesTotal ?? 0) + (isQuickClear ? 0 : 1),
                BuyCount = previousStageData?.BuyCount ?? 0,
                Score = previousStageData?.Score ?? 0,
                LastPassTime = isQuickClear ? (previousStageData?.LastPassTime ?? 0) : now,
                RefreshTime = isQuickClear ? (previousStageData?.RefreshTime ?? 0) : now,
                CreateTime = previousStageData is not null && previousStageData.CreateTime > 0 ? previousStageData.CreateTime : now,
                BestRecordTime = previousStageData?.BestRecordTime ?? 0,
                LastRecordTime = previousStageData?.LastRecordTime ?? 0,
                BestCardIds = isQuickClear
                    ? (previousStageData?.BestCardIds ?? [])
                    : (bestCardIds.Count > 0 ? bestCardIds : (previousStageData?.BestCardIds ?? [])),
                LastCardIds = isQuickClear
                    ? (previousStageData?.LastCardIds ?? [])
                    : bestCardIds
            };
        }

        private static void SendMainLineLuosaitaSectionInfoIfCaptured(Session session, int stageId)
        {
            if (!MainLineLuosaitaPayloadFactory.TryBuildStageProgressSectionInfo(stageId, out MainLineLuosaitaSectionInfo sectionInfo))
                return;

            session.SendPush(new NotifyMainLineLuosaitaSectionInfo
            {
                SectionInfo = sectionInfo
            });
        }
        private static int GetStageTeamExp(StageTable stageTable, bool isFirstClear)
        {
            int stageExp = stageTable.TeamExp ?? 0;
            if (isFirstClear)
            {
                return stageTable.FirstTeamExp ?? stageExp;
            }

            return stageExp;
        }

        private static int GetStageCardExp(StageTable stageTable, bool isFirstClear)
        {
            int stageExp = stageTable.CardExp ?? 0;
            if (isFirstClear)
            {
                return stageTable.FirstCardExp ?? stageExp;
            }

            return stageExp;
        }
    }
}
