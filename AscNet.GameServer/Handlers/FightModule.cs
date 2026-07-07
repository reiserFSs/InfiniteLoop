using AscNet.Common;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Handlers.Drops;
using AscNet.Table.V2.share.character.skill;
using AscNet.Table.V2.share.fuben;
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
            session.SendResponse(new LoadCompleteResponse(), packet.Id);
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
            session.SendResponse(new LeaveFightResponse(), packet.Id);
        }

        [RequestPacketHandler("PreFightRequest")]
        public static void PreFightRequestHandler(Session session, Packet.Request packet)
        {
            PreFightRequest req = MessagePackSerializer.Deserialize<PreFightRequest>(packet.Content);

            StageTable? stageTable = TableReaderV2.Parse<StageTable>().Find(x => x.StageId == req.PreFightData.StageId);
            if (stageTable is null && !MainLineLuosaitaPayloadFactory.HasCapturedStageProgress((int)req.PreFightData.StageId))
            {
                string cardIds = req.PreFightData.CardIds is null
                    ? "<null>"
                    : string.Join(",", req.PreFightData.CardIds);
                session.log.Warn($"[STAGE-PROBE] PreFightStageTableMissing stageId={req.PreFightData.StageId} challengeCount={req.PreFightData.ChallengeCount} cardIds={cardIds}");
            }

            var levelControl = stageTable is null
                ? null
                : TableReaderV2.Parse<Table.V2.share.fuben.StageLevelControlTable>()
                    .Where(x => x.StageId == stageTable.StageId)
                    .OrderBy(x => Math.Abs(session.player.PlayerData.Level - x.MaxLevel))
                    .FirstOrDefault();

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
            List<int> robotIds = stageTable?.RobotId
                ?? req.PreFightData.RobotIds?.Where(robotId => robotId > 0).ToList()
                ?? new();

            if (robotIds.Count > 0)
            {
                HashSet<int> deployableRobotIds = TableReaderV2.Parse<RobotTable>()
                    .Where(robot => robot.CharacterId is > 0)
                    .Select(robot => robot.Id)
                    .ToHashSet();
                robotIds = robotIds.Where(deployableRobotIds.Contains).ToList();
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
                var characterData = session.character.Characters.FirstOrDefault(x => x.Id == cardId);
                if (characterData is null)
                    continue;

                rsp.FightData.RoleData.First(x => x.Id == session.player.PlayerData.Id).NpcData.Add(i, new
                {
                    Character = characterData,
                    Equips = session.character.Equips.Where(x => x.CharacterId == cardId)
                });
            }

            if (robotIds.Count > 0
                && (stageTable is null || req.PreFightData.CardIds is null || cardIdsToDeploy.Count == 0 || (cardIdsToDeploy.Count + robotIds.Count) == 3))
            {
                int npcKey = rsp.FightData.RoleData.First(x => x.Id == session.player.PlayerData.Id).NpcData.Keys.Count;
                foreach (var robotId in robotIds)
                {
                    RobotTable? robot = TableReaderV2.Parse<RobotTable>().Find(x => x.Id == robotId);
                    if (robot is null)
                    {
                        session.log.Warn($"[STAGE-PROBE] PreFightRobotTableMissing stageId={req.PreFightData.StageId} robotId={robotId}");
                        continue;
                    }

                    CharacterSkillTable? characterSkill = TableReaderV2.Parse<CharacterSkillTable>().Find(x => x.CharacterId == robot.CharacterId);
                    IEnumerable<int> skills = characterSkill?.SkillGroupId.SelectMany(x => TableReaderV2.Parse<CharacterSkillGroupTable>().Find(y => y.Id == x)?.SkillId ?? new List<int>()) ?? new List<int>();
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
                            FashionId = (uint)Convert.ToInt32(robot.FashionId),
                            CreateTime = 0,
                            TrustLv = 1,
                            TrustExp = 0,
                            Ability = robot.ShowAbility ?? 0,
                            LiberateLv = robot.LiberateLv ?? 0,
                            CharacterHeadInfo = new()
                            {
                                HeadFashionId = (uint)robot.FashionId
                            }
                        },
                        Equips = equips
                    });
                    npcKey++;
                }
            }

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
            StageTable? stageTable = TableReaderV2.Parse<StageTable>().FirstOrDefault(x => x.StageId == req.Result.StageId);
            if (stageTable is null && !MainLineLuosaitaPayloadFactory.HasCapturedStageProgress((int)req.Result.StageId))
            {
                session.log.Warn($"[STAGE-PROBE] FightSettleStageTableMissing stageId={req.Result.StageId} fightId={req.Result.FightId}");
            }

            StageLevelControlTable? levelControl = stageTable is null
                ? null
                : TableReaderV2.Parse<StageLevelControlTable>()
                    .Where(x => x.StageId == stageTable.StageId)
                    .OrderBy(x => Math.Abs(session.player.PlayerData.Level - x.MaxLevel))
                    .FirstOrDefault();
            int challengeCount = session.fight?.PreFight.PreFightData.ChallengeCount ?? 1;
            uint responseStageId = ResolveFightSettleStageId(session, req);
            bool isQuickClear = responseStageId != req.Result.StageId;
            bool isFirstClear = !session.stage.Stages.ContainsKey(responseStageId);
            bool isSuccessfulSettle = req.Result.IsWin && !req.Result.IsForceExit;
            if (!isSuccessfulSettle)
            {
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
            StageDatum stageData = BuildFightSettleStageDatum(
                responseStageId,
                isQuickClear ? 0 : 7,
                bestCardIds,
                isQuickClear);
            session.stage.AddStage(stageData);

            if (isQuickClear && MainLineLuosaitaPayloadFactory.HasCapturedStageProgress((int)req.Result.StageId))
            {
                StageDatum luosaitaProgressStageData = BuildFightSettleStageDatum(req.Result.StageId, 7, bestCardIds, false);
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
                    Achievement = req.Result.AddStars,
                    RewardGoodsList = multiRewards.Count > 0 ? multiRewards.First() : [],
                    LeftTime = (int)req.Result.LeftTime,
                    NpcHpInfo = req.Result.NpcHpInfo,
                    MultiRewardGoodsList = multiRewards,
                    ChallengeCount = isQuickClear ? 0 : challengeCount
                }
            };

            session.fight = null;
            session.SendPush(new NotifyStageData() { StageList = new() { stageData } });
            SendMainLineLuosaitaSectionInfoIfCaptured(session, (int)req.Result.StageId);
            TaskModule.SendStoryTaskSync(session);
            session.SendResponse(fightSettleResponse, packet.Id);
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

        private static StageDatum BuildFightSettleStageDatum(uint stageId, long starsMark, List<long> bestCardIds, bool isQuickClear)
        {
            long now = DateTimeOffset.Now.ToUnixTimeSeconds();
            return new StageDatum
            {
                StageId = stageId,
                StarsMark = starsMark,
                Achievement = 0,
                Passed = true,
                PassTimesToday = 0,
                PassTimesTotal = isQuickClear ? 0 : 1,
                BuyCount = 0,
                Score = 0,
                LastPassTime = isQuickClear ? 0 : now,
                RefreshTime = isQuickClear ? 0 : now,
                CreateTime = now,
                BestRecordTime = 0,
                LastRecordTime = 0,
                BestCardIds = isQuickClear ? [] : bestCardIds,
                LastCardIds = isQuickClear ? [] : bestCardIds
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
