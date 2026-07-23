using AscNet.Common;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Handlers.Drops;
using AscNet.Table.V2.share.config;
using AscNet.Table.V2.share.equip;
using AscNet.Table.V2.share.partner;
using AscNet.Table.V2.share.team;
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
        DrawTicket = 22,
        ChatBoard = 26,
        FashionColor = 34
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
        public long Value { get; set; }
        public long MaxValue { get; set; }
        public int RoleId { get; set; }
        public int NpcId { get; set; }
        public int CharacterId { get; set; }
        public long DamageTotal { get; set; }
        public long DamageNormal { get; set; }
        public List<long> DamageMagic { get; set; } = new();
        public long BreakEndure { get; set; }
        public long Cure { get; set; }
        public long Hurt { get; set; }
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
    public sealed class FightSettleHeaderRequest
    {
        public FightSettleHeaderResult? Result { get; set; }
    }

    [MessagePackObject(true)]
    public sealed class FightSettleHeaderResult
    {
        public bool IsWin { get; set; }
        public bool IsForceExit { get; set; }
        public uint StageId { get; set; }
        public long FightId { get; set; }
        public long LeftTime { get; set; }
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
    public class FightRestartRequest
    {
        public int FightId { get; set; }
    }

    [MessagePackObject(true)]
    public class FightRestartResponse
    {
        public int Code { get; set; }
        public int Seed { get; set; }
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
    public class SegmentCheckFightResponse
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
        private const int TeamManagerSetTeamParaError = 20004003;
        private const int FightAuthorizationError = 1033;
        private enum TeamPrefabValidationFailure
        {
            None,
            MissingPayload,
            InvalidTeamId,
            InvalidTeamName,
            InvalidTeamShape,
            InvalidMetadata,
            InvalidPositions,
            UnknownCharacter,
            DuplicateCharacter,
            InvalidGeneralSkill,
            InvalidNestedPosition,
            UnknownEquip,
            InvalidEquip,
            DuplicateEquip,
            UnknownPartner,
            DuplicatePartner,
            MissingPartnerSkillData,
            InvalidPartnerSkillTypes,
            InvalidPartnerMainSkillCount,
            UnknownPartnerSkillConfig,
            UnknownPartnerQuality,
            LockedPartnerMainSkill,
            InvalidPartnerPassiveLimit,
            TooManyPartnerPassiveSkills,
            DuplicatePartnerPassiveSkills,
            InvalidPartnerPassiveSkill,
            InvalidTags,
            InvalidSwitchSkill
        }

        private static readonly Lazy<HashSet<uint>> MainLine2AchievementStageIds = new(() => TableReaderV2.Parse<MainLine2StageTable>()
            .Select(stage => (uint)stage.Id)
            .ToHashSet());

        private static readonly Lazy<IReadOnlyDictionary<string, int>> TeamConfigValues = new(() =>
            TableReaderV2.Parse<TeamConfigTable>().ToDictionary(row => row.Key, row => row.Value));
        private static readonly Lazy<HashSet<int>> TeamPrefabTagIds = new(() =>
            TableReaderV2.Parse<TeamPrefabTagTable>().Select(row => row.Id).ToHashSet());
        private static readonly Lazy<IReadOnlyDictionary<uint, EquipTable>> EquipRowsById = new(() =>
            TableReaderV2.Parse<EquipTable>().ToDictionary(row => (uint)row.Id));
        private static readonly Lazy<IReadOnlyDictionary<int, EquipResonanceTable>> EquipResonanceRowsById = new(() =>
            TableReaderV2.Parse<EquipResonanceTable>().ToDictionary(row => row.Id));
        private static readonly Lazy<IReadOnlyDictionary<(int PoolId, int CharacterId), HashSet<int>>> WeaponSkillIdsByPoolAndCharacter = new(() =>
            TableReaderV2.Parse<WeaponSkillPoolTable>()
                .GroupBy(row => (row.PoolId, row.CharacterId))
                .ToDictionary(
                    group => group.Key,
                    group => group.SelectMany(row => row.SkillId).Where(skillId => skillId > 0).ToHashSet()));
        private static readonly Lazy<IReadOnlyDictionary<int, CharacterSkillTable>> CharacterSkillRowsById = new(() =>
            TableReaderV2.Parse<CharacterSkillTable>().ToDictionary(row => row.CharacterId));
        private static readonly Lazy<IReadOnlyDictionary<int, CharacterSkillGroupTable>> CharacterSkillGroupRowsById = new(() =>
            TableReaderV2.Parse<CharacterSkillGroupTable>().ToDictionary(row => row.Id));
        private static readonly Lazy<IReadOnlyDictionary<int, CharacterGeneralSkillTable>> CharacterGeneralSkillRowsById = new(() =>
            TableReaderV2.Parse<CharacterGeneralSkillTable>().ToDictionary(row => row.Id));
        private static readonly Lazy<IReadOnlyDictionary<int, PartnerSkillTable>> PartnerSkillRowsById = new(() =>
            TableReaderV2.Parse<PartnerSkillTable>().ToDictionary(row => row.PartnerId));
        private static readonly Lazy<IReadOnlyDictionary<int, PartnerMainSkillGroupTable>> PartnerMainSkillGroupRowsById = new(() =>
            TableReaderV2.Parse<PartnerMainSkillGroupTable>().ToDictionary(row => row.Id));
        private static readonly Lazy<IReadOnlyDictionary<int, PartnerPassiveSkillGroupTable>> PartnerPassiveSkillGroupRowsById = new(() =>
            TableReaderV2.Parse<PartnerPassiveSkillGroupTable>().ToDictionary(row => row.Id));
        private static readonly Lazy<IReadOnlyDictionary<(int PartnerId, int Quality), PartnerQualityTable>> PartnerQualityRowsByIdAndQuality = new(() =>
            TableReaderV2.Parse<PartnerQualityTable>()
                .ToDictionary(row => (row.PartnerId, row.Quality)));

        private static int TeamMaxPosition => TeamConfigValues.Value["TeamMaxPos"];
        private static int TeamPrefabLimit => TeamConfigValues.Value["MaxTeamPrefab"];

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

        [RequestPacketHandler("SegmentCheckFightRequest")]
        public static void SegmentCheckFightRequestHandler(Session session, Packet.Request packet)
        {
            session.SendResponse(new SegmentCheckFightResponse(), packet.Id);
        }

        [RequestPacketHandler("LeaveFightRequest")]
        public static void LeaveFightRequestHandler(Session session, Packet.Request packet)
        {
            BossModule.CancelFight(session);
            session.PendingBossInshotFight = null;
            session.fight = null;
            session.SendResponse(new LeaveFightResponse(), packet.Id);
        }

        [RequestPacketHandler("PreFightRequest")]
        public static void PreFightRequestHandler(Session session, Packet.Request packet)
        {
            PreFightRequest req = MessagePackSerializer.Deserialize<PreFightRequest>(packet.Content);

            StageTable? stageTable = ResolveStageTable(req.PreFightData.StageId, out bool isCurrentStudyStage);
            if (stageTable is null
                && !BossModule.IsStage(req.PreFightData.StageId)
                && !BossInshotModule.IsStage(req.PreFightData.StageId)
                && !(ArenaModule.IsArenaStage(req.PreFightData.StageId) && req.PreFightData.SelectAreaId > 0)
                && !RepeatChallengeModule.IsStage(req.PreFightData.StageId)
                && !MainLineLuosaitaPayloadFactory.HasCapturedStageProgress((int)req.PreFightData.StageId))
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
                    FightId = (uint)Random.Shared.NextInt64(
                        Math.Clamp(req.PreFightData.StageId, 1u, (uint)int.MaxValue),
                        (long)int.MaxValue + 1),
                    OnlineMode = 0,
                    Seed = (uint)Random.Shared.NextInt64(0, uint.MaxValue),
                    StageId = req.PreFightData.StageId,
                    RebootId = stageTable?.RebootId ?? 0,
                    PassTimeLimit = stageTable?.PassTimeLimit ?? 0,
                    StarsMark = 0,
                    MonsterLevel = levelControl?.MonsterLevel ?? new()
                }
            };

            if (TransfiniteModule.ApplyPreFight(session, req.PreFightData, out int transfiniteCode))
            {
                rsp.Code = transfiniteCode;
                session.SendResponse(rsp, packet.Id);
                return;
            }

            bool isFashionStage = FashionStoryModule.TryValidateStage(
                session, (int)req.PreFightData.StageId, out int fashionCode);
            if (isFashionStage)
            {
                bool isTrialStage = FashionStoryModule.IsTrialStage(session, (int)req.PreFightData.StageId);
                if (fashionCode != 0 || !isTrialStage)
                {
                    rsp.Code = fashionCode != 0 ? fashionCode : FashionStoryModule.StageLocked;
                    session.SendResponse(rsp, packet.Id);
                    return;
                }

                req.PreFightData.ChallengeCount = 1;
                req.PreFightData.CardIds = [];
            }

            if (BossInshotModule.ValidatePreFightRequest(session, req.PreFightData, out int bossInshotValidationCode)
                && bossInshotValidationCode != 0)
            {
                rsp.Code = bossInshotValidationCode;
                session.SendResponse(rsp, packet.Id);
                return;
            }

            RepeatChallengeModule.ApplyPreFight(session.player, rsp.FightData);
            if (req.PreFightData.SelectAreaId > 0 && !ArenaModule.ApplyPreFight(session, req.PreFightData, rsp))
            {
                rsp.Code = 20044029;
                session.SendResponse(rsp, packet.Id);
                return;
            }


            rsp.FightData.RoleData.Add(new()
            {
                Id = (uint)session.player.PlayerData.Id,
                Camp = 1,
                Name = session.player.PlayerData.Name,
                IsRobot = false,
                CaptainIndex = req.PreFightData.CaptainPos > 0 ? req.PreFightData.CaptainPos - 1 : 0,
                FirstFightPos = req.PreFightData.FirstFightPos > 0 ? req.PreFightData.FirstFightPos - 1 : 0,
                EnterCgIndex = req.PreFightData.EnterCgIndex - 1,
                SettleCgIndex = req.PreFightData.SettleCgIndex - 1,
                NpcData = new()
            });

            IReadOnlyList<uint> cardIdsToDeploy = req.PreFightData.CardIds ?? [];
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
            else if (BossInshotModule.IsStage(req.PreFightData.StageId))
            {
                robotIds = requestedRobotIds;
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

            if (robotIds.Count == 0 && !cardIdsToDeploy.Any(cardId => cardId > 0))
            {
                int currentTeamId = (int)session.player.PlayerData.CurrTeamId;
                if (session.player.TeamGroups.TryGetValue(currentTeamId, out TeamGroupDatum? teamGroup))
                {
                    cardIdsToDeploy = teamGroup.TeamData
                        .OrderBy(member => member.Key)
                        .Select(member => (uint)member.Value)
                        .ToList();
                }
            }

            Dictionary<int, dynamic> playerNpcData = rsp.FightData.RoleData
                .First(role => role.Id == session.player.PlayerData.Id)
                .NpcData;

            long currentUnixTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            for (int i = 0; i < cardIdsToDeploy.Count; i++)
            {
                uint cardId = cardIdsToDeploy[i];
                if (cardId == 0)
                    continue;
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

                PartnerData? partner = session.character.Partners.FirstOrDefault(x => x.CharacterId == characterData.Id);
                playerNpcData.Add(i, new
                {
                    Character = characterData,
                    Equips = equips,
                    WeaponFashionId = session.character.WeaponFashions
                        .Find(fashion =>
                            (fashion.ExpireTime == 0 || fashion.ExpireTime > currentUnixTime)
                            && fashion.UseCharacterList.Contains((int)characterData.Id))
                        ?.Id ?? 0,
                    Partner = partner
                });
            }

            int deployedCharacterCount = playerNpcData.Count;
            if (robotIds.Count > 0
                && (stageTable is null || req.PreFightData.CardIds is null || deployedCharacterCount == 0 || (deployedCharacterCount + robotIds.Count) == 3))
            {
                int npcKey = 0;
                foreach (var robotId in robotIds)
                {
                    RobotTable? robot = robotRowsToDeploy.GetValueOrDefault(robotId);
                    if (robot is null)
                    {
                        session.log.Warn($"[STAGE-PROBE] PreFightRobotTableMissing stageId={req.PreFightData.StageId} robotId={robotId}");
                        continue;
                    }

                    while (playerNpcData.ContainsKey(npcKey))
                        npcKey++;

                    CharacterSkillTable? characterSkill = TableReaderV2.Parse<CharacterSkillTable>().Find(x => x.CharacterId == robot.CharacterId);
                    IEnumerable<int> skills = characterSkill?.SkillGroupId.SelectMany(x => TableReaderV2.Parse<CharacterSkillGroupTable>().Find(y => y.Id == x)?.SkillId ?? new List<int>()) ?? new List<int>();
                    HashSet<int> removedSkillIds = robot.RemoveSkillId?.ToHashSet() ?? [];
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

                    playerNpcData.Add(npcKey, new
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
                            SkillList = skills.Where(x => !removedSkillIds.Contains(x)).Select(x => new CharacterSkill() { Id = (uint)x, Level = Math.Min(Convert.ToInt32(robot.SkillLevel), TableReaderV2.Parse<CharacterSkillLevelEffectTable>().OrderByDescending(x => x.Level).FirstOrDefault(y => y.SkillId == x)?.Level ?? 1) }).ToList(),
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
                        WeaponFashionId = robot.WeaponFashion ?? 0,
                        Partner = (PartnerData?)null,
                        IsRobot = true,
                        RobotId = robotId,
                        IsNpc = false,
                    });
                    npcKey++;
                }
            }

            if (isTheatreFight)
                BiancaTheatreModule.ApplyTheatreFightStageData(session, req.PreFightData.StageId, rsp);

            bool isBossInshotFight = BossInshotModule.ApplyPreFight(session, req.PreFightData, rsp, out int bossInshotCode);
            if (isBossInshotFight)
            {
                if (bossInshotCode != 0)
                {
                    rsp.Code = bossInshotCode;
                    session.SendResponse(rsp, packet.Id);
                    return;
                }
            }

            if (BossModule.IsStage(req.PreFightData.StageId)
                && !BossModule.ApplyPreFight(session, req.PreFightData, rsp))
            {
                rsp.Code = 1;
                session.SendResponse(rsp, packet.Id);
                return;
            }

            if (!isBossInshotFight)
                session.PendingBossInshotFight = null;

            if (!TransfiniteModule.TryCommitPreFight(session, req.PreFightData.StageId, out int transfiniteCommitCode))
            {
                rsp.Code = transfiniteCommitCode;
                session.SendResponse(rsp, packet.Id);
                return;
            }

            session.fight = new(req, rsp.FightData.FightId);
            session.SendResponse(rsp, packet.Id);
        }

        [RequestPacketHandler("FightRebootRequest")]
        public static void HandleFightRebootRequestHandler(Session session, Packet.Request packet)
        {
            FightRebootRequest req = MessagePackSerializer.Deserialize<FightRebootRequest>(packet.Content);
            session.SendResponse(new FightRebootResponse(), packet.Id);
        }

        [RequestPacketHandler("FightRestartRequest")]
        public static void HandleFightRestartRequestHandler(Session session, Packet.Request packet)
        {
            FightRestartRequest req = MessagePackSerializer.Deserialize<FightRestartRequest>(packet.Content);
            if (session.fight is null || session.fight.FightId != unchecked((uint)req.FightId))
            {
                session.SendResponse(new FightRestartResponse { Code = FightAuthorizationError }, packet.Id);
                return;
            }

            session.SendResponse(new FightRestartResponse
            {
                Code = 0,
                Seed = (int)Random.Shared.NextInt64(int.MinValue, (long)int.MaxValue + 1)
            }, packet.Id);
        }

        [RequestPacketHandler("EnterStoryRequest")]
        public static void HandleEnterStoryRequestHandler(Session session, Packet.Request packet)
        {
            EnterStoryRequest req = packet.Deserialize<EnterStoryRequest>();
            EnterStoryResponse response = new();

            if (req.StageId <= 0 || !TableReaderV2.Parse<StageTable>().Any(stage => stage.StageId == req.StageId))
            {
                response.Code = FashionStoryModule.StageNotFound;
                session.SendResponse(response, packet.Id);
                return;
            }

            bool isFashionStage = FashionStoryModule.TryValidateStage(session, req.StageId, out int validationCode);
            if (isFashionStage
                && (validationCode != 0 || FashionStoryModule.IsTrialStage(session, req.StageId)))
            {
                response.Code = validationCode != 0 ? validationCode : FashionStoryModule.StageLocked;
                session.SendResponse(response, packet.Id);
                return;
            }

            if (!session.stage.Stages.TryGetValue(req.StageId, out StageDatum? stageData) || !stageData.Passed)
            {
                stageData = new StageDatum
                {
                    StageId = req.StageId,
                    Passed = true,
                    CreateTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    BestCardIds = [],
                    LastCardIds = []
                };
                session.stage.AddStage(stageData);
                session.stage.Save();
            }

            session.SendPush(new NotifyStageData { StageList = [stageData] });
            SendMainLineLuosaitaSectionInfoIfCaptured(session, req.StageId);
            session.SendResponse(response, packet.Id);
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

        [RequestPacketHandler("TeamPrefabSetTeamRequest")]
        public static void TeamPrefabSetTeamRequestHandler(Session session, Packet.Request packet)
        {
            TeamPrefabSetTeamRequest? request = packet.Deserialize<TeamPrefabSetTeamRequest>();
            if (!TryNormalizeTeamPrefab(
                    session,
                    request?.TeamPrefabData,
                    out TeamPrefabData teamPrefab,
                    out TeamPrefabValidationFailure failure))
            {
                string detail = IsPartnerSkillValidationFailure(failure)
                    ? DescribePartnerSkillData(session, request?.TeamPrefabData)
                    : string.Empty;
                session.log.Warn(
                    $"TeamPrefabSetTeam rejected TeamId={request?.TeamPrefabData?.TeamId}: {failure}{detail}");
                SendInvalidTeamPrefabResponse(session, packet.Id);
                return;
            }

            session.player.NormalizeTeamPrefabs();
            List<TeamPrefabData> teamPrefabs = session.player.TeamPrefabs;
            int existingIndex = teamPrefabs.FindIndex(value => value.TeamId == teamPrefab.TeamId);
            if (existingIndex < 0 && teamPrefabs.Count >= TeamPrefabLimit)
            {
                SendInvalidTeamPrefabResponse(session, packet.Id);
                return;
            }

            if (existingIndex < 0)
                teamPrefabs.Add(teamPrefab);
            else
                teamPrefabs[existingIndex] = teamPrefab;

            session.player.TeamPrefabs = teamPrefabs;
            session.player.Save();
            session.SendResponse(new TeamPrefabSetTeamResponse(), packet.Id);
        }

        [RequestPacketHandler("TeamPrefabDelRequest")]
        public static void TeamPrefabDelRequestHandler(Session session, Packet.Request packet)
        {
            TeamPrefabDelRequest? request = packet.Deserialize<TeamPrefabDelRequest>();
            session.player.NormalizeTeamPrefabs();
            int index = request is null
                ? -1
                : session.player.TeamPrefabs.FindIndex(team => team.TeamId == request.TeamId);
            if (index < 0)
            {
                session.SendResponse(new TeamPrefabDelResponse { Code = TeamManagerSetTeamParaError }, packet.Id);
                return;
            }

            session.player.TeamPrefabs.RemoveAt(index);
            session.player.Save();
            session.SendResponse(new TeamPrefabDelResponse(), packet.Id);
        }

        [RequestPacketHandler("TeamPrefabMoveForwardRequest")]
        public static void TeamPrefabMoveForwardRequestHandler(Session session, Packet.Request packet)
        {
            TeamPrefabMoveForwardRequest? request = packet.Deserialize<TeamPrefabMoveForwardRequest>();
            session.player.NormalizeTeamPrefabs();
            int index = request is null
                ? -1
                : session.player.TeamPrefabs.FindIndex(team => team.TeamId == request.TeamId);
            if (index < 0)
            {
                session.SendResponse(new TeamPrefabMoveForwardResponse { Code = TeamManagerSetTeamParaError }, packet.Id);
                return;
            }

            if (index > 0)
                (session.player.TeamPrefabs[index - 1], session.player.TeamPrefabs[index]) =
                    (session.player.TeamPrefabs[index], session.player.TeamPrefabs[index - 1]);
            session.player.Save();
            session.SendResponse(new TeamPrefabMoveForwardResponse(), packet.Id);
        }

        [RequestPacketHandler("TeamPrefabSetTagsRequest")]
        public static void TeamPrefabSetTagsRequestHandler(Session session, Packet.Request packet)
        {
            TeamPrefabSetTagsRequest? request = packet.Deserialize<TeamPrefabSetTagsRequest>();
            session.player.NormalizeTeamPrefabs();
            int index = request is null
                ? -1
                : session.player.TeamPrefabs.FindIndex(team => team.TeamId == request.TeamId);
            if (index < 0)
            {
                session.SendResponse(new TeamPrefabSetTagsResponse { Code = TeamManagerSetTeamParaError }, packet.Id);
                return;
            }

            TeamPrefabData source = session.player.TeamPrefabs[index];
            TeamPrefabData candidate = new()
            {
                TeamType = source.TeamType,
                TeamId = source.TeamId,
                CaptainPos = source.CaptainPos,
                FirstFightPos = source.FirstFightPos,
                EnterCgIndex = source.EnterCgIndex,
                SettleCgIndex = source.SettleCgIndex,
                TeamData = source.TeamData,
                TeamName = source.TeamName,
                SelectedGeneralSkill = source.SelectedGeneralSkill,
                PartnerData = source.PartnerData,
                EquipData = source.EquipData,
                TagsSet = request!.Tags ?? [],
                SwitchSkills = source.SwitchSkills
            };
            if (!TryNormalizeTeamPrefab(session, candidate, out TeamPrefabData normalized, out _))
            {
                session.SendResponse(new TeamPrefabSetTagsResponse { Code = TeamManagerSetTeamParaError }, packet.Id);
                return;
            }

            session.player.TeamPrefabs[index] = normalized;
            session.player.Save();
            session.SendResponse(new TeamPrefabSetTagsResponse(), packet.Id);
        }

        [RequestPacketHandler("TeamPrefabUpdateEquipRequest")]
        public static void TeamPrefabUpdateEquipRequestHandler(Session session, Packet.Request packet)
        {
            TeamPrefabUpdateEquipRequest? request = packet.Deserialize<TeamPrefabUpdateEquipRequest>();
            session.player.NormalizeTeamPrefabs();
            int index = request is null
                ? -1
                : session.player.TeamPrefabs.FindIndex(team => team.TeamId == request.TeamId);
            if (index < 0 || request!.TeamPrefabEquipData is null)
            {
                session.SendResponse(new TeamPrefabUpdateEquipResponse { Code = TeamManagerSetTeamParaError }, packet.Id);
                return;
            }

            TeamPrefabData source = session.player.TeamPrefabs[index];
            if (!source.TeamData.ContainsKey(request.TeamPos))
            {
                session.SendResponse(new TeamPrefabUpdateEquipResponse { Code = TeamManagerSetTeamParaError }, packet.Id);
                return;
            }

            Dictionary<int, TeamPrefabEquipData?> equipData = source.EquipData
                .ToDictionary(pair => pair.Key, pair => pair.Value);
            equipData[request.TeamPos] = request.TeamPrefabEquipData;
            TeamPrefabData candidate = new()
            {
                TeamType = source.TeamType,
                TeamId = source.TeamId,
                CaptainPos = source.CaptainPos,
                FirstFightPos = source.FirstFightPos,
                EnterCgIndex = source.EnterCgIndex,
                SettleCgIndex = source.SettleCgIndex,
                TeamData = source.TeamData,
                TeamName = source.TeamName,
                SelectedGeneralSkill = source.SelectedGeneralSkill,
                PartnerData = source.PartnerData,
                EquipData = equipData,
                TagsSet = source.TagsSet,
                SwitchSkills = source.SwitchSkills
            };
            if (!TryNormalizeTeamPrefab(session, candidate, out TeamPrefabData normalized, out _))
            {
                session.SendResponse(new TeamPrefabUpdateEquipResponse { Code = TeamManagerSetTeamParaError }, packet.Id);
                return;
            }

            session.player.TeamPrefabs[index] = normalized;
            session.player.Save();
            session.SendResponse(new TeamPrefabUpdateEquipResponse(), packet.Id);
        }

        [RequestPacketHandler("TeamPrefabSetPartnerRequest")]
        public static void TeamPrefabSetPartnerRequestHandler(Session session, Packet.Request packet)
        {
            TeamPrefabSetPartnerRequest? request = packet.Deserialize<TeamPrefabSetPartnerRequest>();
            session.player.NormalizeTeamPrefabs();
            List<TeamPrefabData> teamPrefabs = session.player.TeamPrefabs;
            int teamPrefabIndex = request is null
                ? -1
                : teamPrefabs.FindIndex(value => value.TeamId == request.TeamId);
            if (request is null
                || teamPrefabIndex < 0
                || !teamPrefabs[teamPrefabIndex].TeamData.TryGetValue(
                    request.TeamPos,
                    out int characterId)
                || characterId <= 0)
            {
                SendInvalidTeamPrefabPartnerResponse(session, packet.Id);
                return;
            }

            TeamPrefabData source = teamPrefabs[teamPrefabIndex];
            Dictionary<int, TeamPrefabPartnerData?> partnerData = source.PartnerData?
                .ToDictionary(pair => pair.Key, pair => pair.Value)
                ?? [];
            partnerData[request.TeamPos] = request.TeamPrefabPartnerData;
            TeamPrefabData candidate = new()
            {
                TeamType = source.TeamType,
                TeamId = source.TeamId,
                CaptainPos = source.CaptainPos,
                FirstFightPos = source.FirstFightPos,
                EnterCgIndex = source.EnterCgIndex,
                SettleCgIndex = source.SettleCgIndex,
                TeamData = source.TeamData,
                TeamName = source.TeamName,
                SelectedGeneralSkill = source.SelectedGeneralSkill,
                PartnerData = partnerData,
                EquipData = source.EquipData,
                TagsSet = source.TagsSet,
                SwitchSkills = source.SwitchSkills
            };
            if (!TryNormalizeTeamPrefab(
                    session,
                    candidate,
                    out TeamPrefabData normalized,
                    out TeamPrefabValidationFailure failure))
            {
                string detail = IsPartnerSkillValidationFailure(failure)
                    ? DescribePartnerSkillData(session, candidate)
                    : string.Empty;
                session.log.Warn(
                    $"TeamPrefabSetPartner rejected TeamId={request.TeamId}, TeamPos={request.TeamPos}: " +
                    $"{failure}{detail}");
                SendInvalidTeamPrefabPartnerResponse(session, packet.Id);
                return;
            }

            teamPrefabs[teamPrefabIndex] = normalized;
            session.player.TeamPrefabs = teamPrefabs;
            session.player.Save();
            session.SendResponse(new TeamPrefabSetPartnerResponse(), packet.Id);
        }

        private static void SendInvalidTeamPrefabPartnerResponse(Session session, int packetId)
        {
            session.SendResponse(
                new TeamPrefabSetPartnerResponse { Code = TeamManagerSetTeamParaError },
                packetId);
        }

        private static bool IsPartnerSkillValidationFailure(TeamPrefabValidationFailure failure)
        {
            return failure is TeamPrefabValidationFailure.MissingPartnerSkillData
                or TeamPrefabValidationFailure.InvalidPartnerSkillTypes
                or TeamPrefabValidationFailure.InvalidPartnerMainSkillCount
                or TeamPrefabValidationFailure.UnknownPartnerSkillConfig
                or TeamPrefabValidationFailure.UnknownPartnerQuality
                or TeamPrefabValidationFailure.LockedPartnerMainSkill
                or TeamPrefabValidationFailure.InvalidPartnerPassiveLimit
                or TeamPrefabValidationFailure.TooManyPartnerPassiveSkills
                or TeamPrefabValidationFailure.DuplicatePartnerPassiveSkills
                or TeamPrefabValidationFailure.InvalidPartnerPassiveSkill;
        }

        private static string DescribePartnerSkillData(Session session, TeamPrefabData? source)
        {
            if (source?.PartnerData is null)
                return "; PartnerData=null";

            List<string> entries = new();
            foreach ((int position, TeamPrefabPartnerData? presetPartner) in source.PartnerData.OrderBy(pair => pair.Key))
            {
                if (presetPartner is null)
                {
                    entries.Add($"pos={position},value=null");
                    continue;
                }

                PartnerData? ownedPartner = session.character.Partners
                    .FirstOrDefault(partner => partner.Id == presetPartner.PartnerId);
                int? passiveLimit = null;
                if (ownedPartner is not null
                    && PartnerQualityRowsByIdAndQuality.Value.TryGetValue(
                        (ownedPartner.TemplateId, ownedPartner.Quality),
                        out PartnerQualityTable? qualityRow))
                {
                    passiveLimit = qualityRow.SkillColumnCount;
                }

                string mainSkills = presetPartner.SkillData?.TryGetValue(1, out List<int>? main) == true
                    ? main is null ? "null" : $"[{string.Join(',', main)}]"
                    : "missing";
                string passiveSkills = presetPartner.SkillData?.TryGetValue(2, out List<int>? passive) == true
                    ? passive is null ? "null" : $"[{string.Join(',', passive)}]"
                    : "missing";
                entries.Add(
                    $"pos={position},partner={presetPartner.PartnerId},template={ownedPartner?.TemplateId},"
                    + $"quality={ownedPartner?.Quality},passiveLimit={passiveLimit},"
                    + $"main={mainSkills},passive={passiveSkills}");
            }

            return $"; PartnerData={string.Join(" | ", entries)}";
        }

        [RequestPacketHandler("TeamPrefabUpdateMetadataRequest")]
        public static void TeamPrefabUpdateMetadataRequestHandler(Session session, Packet.Request packet)
        {
            TeamPrefabUpdateMetadataRequest? request = packet.Deserialize<TeamPrefabUpdateMetadataRequest>();
            TeamPrefabMetadata? metadata = request?.PrefabTeamInfo;
            List<TeamPrefabData> teamPrefabs = session.player.TeamPrefabs ?? [];
            int teamPrefabIndex = request is null
                ? -1
                : teamPrefabs.FindIndex(value => value?.TeamId == request.TeamId);
            if (metadata is null || teamPrefabIndex < 0)
            {
                SendInvalidTeamPrefabMetadataResponse(session, packet.Id);
                return;
            }

            TeamPrefabData teamPrefab = teamPrefabs[teamPrefabIndex];
            TeamPrefabData metadataCandidate = new()
            {
                TeamId = teamPrefab.TeamId,
                TeamData = teamPrefab.TeamData,
                TeamName = metadata.TeamName,
                CaptainPos = metadata.CaptainPos,
                FirstFightPos = metadata.FirstFightPos,
                EnterCgIndex = metadata.EnterCgIndex,
                SettleCgIndex = metadata.SettleCgIndex,
                SelectedGeneralSkill = metadata.SelectedGeneralSkill
            };
            if (!TryValidateTeamPrefabMetadata(
                    session,
                    metadataCandidate,
                    validateGeneralSkillRequirements: false,
                    out _,
                    out _,
                    out _,
                    out TeamPrefabValidationFailure failure))
            {
                session.log.Warn(
                    $"TeamPrefabUpdateMetadata rejected TeamId={request!.TeamId}: {failure}");
                SendInvalidTeamPrefabMetadataResponse(session, packet.Id);
                return;
            }

            teamPrefab.TeamName = metadata.TeamName;
            teamPrefab.CaptainPos = metadata.CaptainPos;
            teamPrefab.FirstFightPos = metadata.FirstFightPos;
            teamPrefab.EnterCgIndex = metadata.EnterCgIndex;
            teamPrefab.SettleCgIndex = metadata.SettleCgIndex;
            teamPrefab.SelectedGeneralSkill = metadata.SelectedGeneralSkill;
            session.player.Save();
            session.SendResponse(new TeamPrefabUpdateMetadataResponse(), packet.Id);
        }

        [RequestPacketHandler("TeamPrefabApplyRequest")]
        public static void TeamPrefabApplyRequestHandler(Session session, Packet.Request packet)
        {
            TeamPrefabApplyRequest? request = packet.Deserialize<TeamPrefabApplyRequest>();
            TeamPrefabData? source = request is null
                ? null
                : (session.player.TeamPrefabs ?? [])
                    .FirstOrDefault(value => value?.TeamId == request.TeamId);
            if (!TryNormalizeTeamPrefab(
                    session,
                    source,
                    out TeamPrefabData teamPrefab,
                    out TeamPrefabValidationFailure failure))
            {
                session.log.Warn($"TeamPrefabApply rejected TeamId={request?.TeamId}: {failure}");
                SendInvalidTeamPrefabApplyResponse(session, packet.Id);
                return;
            }

            Dictionary<uint, EquipData> ownedEquipsById = session.character.Equips
                .GroupBy(equip => equip.Id)
                .ToDictionary(group => group.Key, group => group.First());
            Dictionary<int, PartnerData> ownedPartnersById = session.character.Partners
                .GroupBy(partner => partner.Id)
                .ToDictionary(group => group.Key, group => group.First());
            Dictionary<int, CharacterData> ownedCharactersById = session.character.Characters
                .GroupBy(character => (int)character.Id)
                .ToDictionary(group => group.Key, group => group.First());
            List<(int CharacterId, EquipData Equip)> equipPlan = new();
            List<(int CharacterId, PartnerData Partner, TeamPrefabPartnerData Preset)> partnerPlan = new();

            foreach ((int position, int characterId) in teamPrefab.TeamData.OrderBy(pair => pair.Key))
            {
                if (characterId <= 0)
                    continue;

                if (teamPrefab.EquipData.TryGetValue(position, out TeamPrefabEquipData? equipData)
                    && equipData is not null)
                {
                    foreach (TeamPrefabEquipEntry presetEquip in equipData.EquipDataDict
                                 .OrderBy(pair => pair.Key)
                                 .Select(pair => pair.Value))
                    {
                        if (!ownedEquipsById.TryGetValue(presetEquip.EquipId, out EquipData? equip))
                        {
                            session.log.Warn(
                                $"TeamPrefabApply rejected stale equip TeamId={teamPrefab.TeamId} " +
                                $"EquipId={presetEquip.EquipId}");
                            SendInvalidTeamPrefabApplyResponse(session, packet.Id);
                            return;
                        }
                        equipPlan.Add((characterId, equip));
                    }
                }

                if (teamPrefab.PartnerData.TryGetValue(position, out TeamPrefabPartnerData? presetPartner)
                    && presetPartner is { PartnerId: > 0 })
                {
                    if (!ownedPartnersById.TryGetValue(presetPartner.PartnerId, out PartnerData? partner))
                    {
                        session.log.Warn(
                            $"TeamPrefabApply rejected stale partner TeamId={teamPrefab.TeamId} " +
                            $"PartnerId={presetPartner.PartnerId}");
                        SendInvalidTeamPrefabApplyResponse(session, packet.Id);
                        return;
                    }
                    partnerPlan.Add((characterId, partner, presetPartner));
                }
            }

            foreach ((int characterId, EquipData equip) in equipPlan)
                ApplyTeamPrefabEquip(session.character, characterId, equip);

            HashSet<int> targetCharacterIds = teamPrefab.TeamData.Values
                .Where(characterId => characterId > 0)
                .ToHashSet();
            Dictionary<int, int> desiredPartnerByCharacter = partnerPlan
                .ToDictionary(entry => entry.CharacterId, entry => entry.Partner.Id);
            HashSet<int> affectedPartnerIds = new();
            bool carryChanged = false;
            foreach (PartnerData partner in session.character.Partners)
            {
                if (partner.CharacterId <= 0
                    || !targetCharacterIds.Contains(partner.CharacterId)
                    || (desiredPartnerByCharacter.TryGetValue(
                            partner.CharacterId,
                            out int desiredPartnerId)
                        && desiredPartnerId == partner.Id))
                {
                    continue;
                }

                partner.CharacterId = 0;
                session.character.NormalizePartnerMainSkillForCarrier(partner);
                affectedPartnerIds.Add(partner.Id);
                carryChanged = true;
            }

            foreach ((int characterId, PartnerData partner, TeamPrefabPartnerData preset) in partnerPlan)
            {
                if (partner.CharacterId != characterId)
                {
                    partner.CharacterId = characterId;
                    carryChanged = true;
                }
                ApplyTeamPrefabPartnerSkills(session.character, partner, preset.SkillData);
                affectedPartnerIds.Add(partner.Id);
            }

            foreach ((int position, int skillId) in teamPrefab.SwitchSkills)
            {
                if (teamPrefab.TeamData.TryGetValue(position, out int characterId)
                    && characterId > 0
                    && ownedCharactersById.TryGetValue(characterId, out CharacterData? character))
                {
                    ApplyTeamPrefabCharacterSwitchSkill(character, skillId);
                }
            }

            session.character.Save();
            session.SendPush(new NotifyPartnerDataList
            {
                PartnerDataList = session.character.Partners
                    .Where(partner => affectedPartnerIds.Contains(partner.Id))
                    .OrderBy(partner => partner.Id)
                    .ToList(),
                OperateTypes = carryChanged ? [2, 3] : [2]
            });
            session.SendResponse(new TeamPrefabApplyRequestResponse(), packet.Id);
        }

        private static void SendInvalidTeamPrefabApplyResponse(Session session, int packetId)
        {
            session.SendResponse(
                new TeamPrefabApplyRequestResponse { Code = TeamManagerSetTeamParaError },
                packetId);
        }

        private static void ApplyTeamPrefabEquip(
            AscNet.Common.Database.Character character,
            int characterId,
            EquipData selectedEquip)
        {
            EquipTable selectedRow = EquipRowsById.Value[selectedEquip.TemplateId];
            int previousCharacterId = selectedEquip.CharacterId;
            EquipData? previousEquip = character.Equips.FirstOrDefault(candidate =>
                candidate.Id != selectedEquip.Id
                && candidate.CharacterId == characterId
                && EquipRowsById.Value.TryGetValue(candidate.TemplateId, out EquipTable? candidateRow)
                && candidateRow.Site == selectedRow.Site);
            if (previousEquip is not null)
                previousEquip.CharacterId = selectedRow.Site == 0 ? previousCharacterId : 0;

            selectedEquip.CharacterId = characterId;
        }

        private static void ApplyTeamPrefabPartnerSkills(
            AscNet.Common.Database.Character character,
            PartnerData partner,
            IReadOnlyDictionary<int, List<int>>? skillData)
        {
            if (skillData?.TryGetValue(1, out List<int>? selectedMainSkills) == true
                && selectedMainSkills is { Count: 1 }
                && partner.SkillList.FirstOrDefault(skill => skill.Type == 1) is PartnerSkillData mainSkill)
            {
                mainSkill.Id = selectedMainSkills[0];
                mainSkill.IsWear = true;
            }

            HashSet<int> selectedPassiveSkills = skillData?.TryGetValue(
                    2,
                    out List<int>? passiveSkills) == true
                ? (passiveSkills ?? []).ToHashSet()
                : [];
            foreach (PartnerSkillData passiveSkill in partner.SkillList.Where(skill => skill.Type == 2))
                passiveSkill.IsWear = selectedPassiveSkills.Contains(passiveSkill.Id);

            character.NormalizePartnerMainSkillForCarrier(partner);
        }

        private static void ApplyTeamPrefabCharacterSwitchSkill(
            CharacterData character,
            int skillId)
        {
            if (!CharacterSkillRowsById.Value.TryGetValue(
                    (int)character.Id,
                    out CharacterSkillTable? characterSkill))
            {
                return;
            }

            CharacterSkillGroupTable? selectedGroup = characterSkill.SkillGroupId
                .Where(CharacterSkillGroupRowsById.Value.ContainsKey)
                .Select(groupId => CharacterSkillGroupRowsById.Value[groupId])
                .FirstOrDefault(group => group.SkillId.Count > 1 && group.SkillId.Contains(skillId));
            if (selectedGroup is null)
                return;

            HashSet<uint> groupSkillIds = selectedGroup.SkillId
                .Where(value => value > 0)
                .Select(value => (uint)value)
                .ToHashSet();
            List<CharacterSkill>? currentSkills = character.SkillList;
            CharacterSkill? current = currentSkills?
                .LastOrDefault(skill => groupSkillIds.Contains(skill.Id));
            if (current is null || current.Id == (uint)skillId)
                return;

            List<CharacterSkill> updatedSkills = new();
            bool inserted = false;
            foreach (CharacterSkill skill in currentSkills!)
            {
                if (!groupSkillIds.Contains(skill.Id))
                {
                    updatedSkills.Add(skill);
                }
                else if (!inserted)
                {
                    updatedSkills.Add(new CharacterSkill
                    {
                        Id = (uint)skillId,
                        Level = current.Level
                    });
                    inserted = true;
                }
            }
            character.SkillList = updatedSkills;
        }

        private static void SendInvalidTeamPrefabMetadataResponse(Session session, int packetId)
        {
            session.SendResponse(
                new TeamPrefabUpdateMetadataResponse { Code = TeamManagerSetTeamParaError },
                packetId);
        }

        private static bool TryValidateTeamPrefabMetadata(
            Session session,
            TeamPrefabData source,
            bool validateGeneralSkillRequirements,
            out int[] positions,
            out Dictionary<int, CharacterData> ownedCharactersById,
            out int[] memberIds,
            out TeamPrefabValidationFailure failure)
        {
            positions = [];
            ownedCharactersById = [];
            memberIds = [];
            failure = TeamPrefabValidationFailure.None;
            if (source.TeamId <= 0)
            {
                failure = TeamPrefabValidationFailure.InvalidTeamId;
                return false;
            }
            if (string.IsNullOrWhiteSpace(source.TeamName) || source.TeamName.Any(char.IsControl))
            {
                failure = TeamPrefabValidationFailure.InvalidTeamName;
                return false;
            }
            if (source.TeamData is null || source.TeamData.Count != TeamMaxPosition)
            {
                failure = TeamPrefabValidationFailure.InvalidTeamShape;
                return false;
            }
            if (source.EnterCgIndex < 0
                || source.EnterCgIndex > TeamMaxPosition
                || source.SettleCgIndex < 0
                || source.SettleCgIndex > TeamMaxPosition
                || source.SelectedGeneralSkill < 0)
            {
                failure = TeamPrefabValidationFailure.InvalidMetadata;
                return false;
            }

            positions = source.TeamData.Keys.Order().ToArray();
            if (!positions.SequenceEqual(Enumerable.Range(1, TeamMaxPosition))
                || source.CaptainPos < 1 || source.CaptainPos > TeamMaxPosition
                || source.FirstFightPos < 1 || source.FirstFightPos > TeamMaxPosition)
            {
                failure = TeamPrefabValidationFailure.InvalidPositions;
                return false;
            }

            Dictionary<int, CharacterData> ownedCharacters = session.character.Characters
                .GroupBy(character => (int)character.Id)
                .ToDictionary(group => group.Key, group => group.First());
            int[] members = source.TeamData.Values.Where(characterId => characterId > 0).ToArray();
            ownedCharactersById = ownedCharacters;
            memberIds = members;
            if (source.TeamData.Values.Any(characterId => characterId < 0)
                || members.Any(characterId => !ownedCharacters.ContainsKey(characterId)))
            {
                failure = TeamPrefabValidationFailure.UnknownCharacter;
                return false;
            }
            if (members.Length != members.Distinct().Count())
            {
                failure = TeamPrefabValidationFailure.DuplicateCharacter;
                return false;
            }
            if (source.SelectedGeneralSkill != 0
                && !CharacterGeneralSkillRowsById.Value.ContainsKey(source.SelectedGeneralSkill))
            {
                failure = TeamPrefabValidationFailure.InvalidGeneralSkill;
                return false;
            }
            if (validateGeneralSkillRequirements
                && !IsValidGeneralSkill(
                    source.SelectedGeneralSkill,
                    members.Select(characterId => ownedCharacters[characterId])))
            {
                failure = TeamPrefabValidationFailure.InvalidGeneralSkill;
                return false;
            }
            return true;
        }

        private static bool TryNormalizeTeamPrefab(
            Session session,
            TeamPrefabData? source,
            out TeamPrefabData normalized,
            out TeamPrefabValidationFailure failure)
        {
            normalized = null!;
            failure = TeamPrefabValidationFailure.None;
            if (source is null)
            {
                failure = TeamPrefabValidationFailure.MissingPayload;
                return false;
            }
            if (!TryValidateTeamPrefabMetadata(
                    session,
                    source,
                    validateGeneralSkillRequirements: true,
                    out int[] positions,
                    out Dictionary<int, CharacterData> ownedCharactersById,
                    out int[] memberIds,
                    out failure))
            {
                return false;
            }

            HashSet<int> validPositions = positions.ToHashSet();
            if ((source.PartnerData?.Keys.Any(position => !validPositions.Contains(position)) ?? false)
                || (source.EquipData?.Keys.Any(position => !validPositions.Contains(position)) ?? false)
                || (source.SwitchSkills?.Keys.Any(position => !validPositions.Contains(position)) ?? false))
            {
                failure = TeamPrefabValidationFailure.InvalidNestedPosition;
                return false;
            }

            Dictionary<uint, EquipData> ownedEquipsById = session.character.Equips
                .GroupBy(equip => equip.Id)
                .ToDictionary(group => group.Key, group => group.First());
            HashSet<uint> presetEquipIds = new();
            foreach ((int position, TeamPrefabEquipData? equipData) in source.EquipData ?? [])
            {
                foreach ((int slot, TeamPrefabEquipEntry equip) in equipData?.EquipDataDict
                             ?? Enumerable.Empty<KeyValuePair<int, TeamPrefabEquipEntry>>())
                {
                    if (equip is null)
                    {
                        failure = TeamPrefabValidationFailure.InvalidEquip;
                        return false;
                    }
                    if (equip.EquipId == 0)
                        continue;
                    if (source.TeamData[position] == 0
                        || !ownedEquipsById.TryGetValue(equip.EquipId, out EquipData? ownedEquip))
                    {
                        failure = TeamPrefabValidationFailure.UnknownEquip;
                        return false;
                    }
                    if (!EquipRowsById.Value.TryGetValue(ownedEquip.TemplateId, out EquipTable? equipRow)
                        || !IsValidTeamPrefabEquipEntry(
                            source.TeamData[position],
                            slot,
                            ownedEquip,
                            equipRow,
                            equip))
                    {
                        failure = TeamPrefabValidationFailure.InvalidEquip;
                        return false;
                    }
                    if (!presetEquipIds.Add(equip.EquipId))
                    {
                        failure = TeamPrefabValidationFailure.DuplicateEquip;
                        return false;
                    }
                }
            }

            Dictionary<int, PartnerData> ownedPartnersById = session.character.Partners
                .GroupBy(partner => partner.Id)
                .ToDictionary(group => group.Key, group => group.First());
            HashSet<int> presetPartnerIds = new();
            foreach ((int position, TeamPrefabPartnerData? partnerData) in source.PartnerData ?? [])
            {
                int partnerId = partnerData?.PartnerId ?? 0;
                if (partnerId == 0)
                    continue;
                if (partnerId < 0
                    || source.TeamData[position] == 0
                    || !ownedPartnersById.TryGetValue(partnerId, out PartnerData? ownedPartner))
                {
                    failure = TeamPrefabValidationFailure.UnknownPartner;
                    return false;
                }
                if (!presetPartnerIds.Add(partnerId))
                {
                    failure = TeamPrefabValidationFailure.DuplicatePartner;
                    return false;
                }
                if (!IsValidPartnerSkillData(
                        ownedPartner,
                        partnerData!.SkillData,
                        out TeamPrefabValidationFailure partnerFailure))
                {
                    failure = partnerFailure;
                    return false;
                }
            }

            List<int> tags = source.TagsSet?.ToList() ?? [];
            if (tags.Any(tagId => !TeamPrefabTagIds.Value.Contains(tagId))
                || tags.Count != tags.Distinct().Count())
            {
                failure = TeamPrefabValidationFailure.InvalidTags;
                return false;
            }

            Dictionary<int, int> switchSkills = source.SwitchSkills?
                .OrderBy(pair => pair.Key)
                .ToDictionary(pair => pair.Key, pair => pair.Value)
                ?? [];
            foreach ((int position, int skillId) in switchSkills)
            {
                int characterId = source.TeamData[position];
                if (skillId <= 0
                    || !ownedCharactersById.TryGetValue(characterId, out CharacterData? character)
                    || !IsValidCharacterSwitchSkill(character, skillId))
                {
                    failure = TeamPrefabValidationFailure.InvalidSwitchSkill;
                    return false;
                }
            }

            Dictionary<int, int> teamDataByPosition = new();
            Dictionary<int, TeamPrefabPartnerData?> partnerDataByPosition = new();
            Dictionary<int, TeamPrefabEquipData?> equipDataByPosition = new();
            foreach (int position in positions)
            {
                int characterId = source.TeamData[position];
                teamDataByPosition[position] = characterId;
                if (characterId == 0)
                {
                    partnerDataByPosition[position] = null;
                    equipDataByPosition[position] = null;
                    continue;
                }

                TeamPrefabPartnerData? sourcePartner = null;
                source.PartnerData?.TryGetValue(position, out sourcePartner);
                partnerDataByPosition[position] = sourcePartner is null || sourcePartner.PartnerId == 0
                    ? new TeamPrefabPartnerData()
                    : new TeamPrefabPartnerData
                    {
                        PartnerId = sourcePartner.PartnerId,
                        SkillData = sourcePartner.SkillData?
                            .OrderBy(pair => pair.Key)
                            .ToDictionary(pair => pair.Key, pair => pair.Value?.ToList() ?? [])
                    };

                TeamPrefabEquipData? sourceEquip = null;
                source.EquipData?.TryGetValue(position, out sourceEquip);
                equipDataByPosition[position] = sourceEquip is null
                    ? null
                    : new TeamPrefabEquipData
                    {
                        EquipDataDict = sourceEquip.EquipDataDict?
                            .Where(pair => pair.Value is not null && pair.Value.EquipId > 0)
                            .OrderBy(pair => pair.Key)
                            .ToDictionary(
                                pair => pair.Key,
                                pair => new TeamPrefabEquipEntry
                                {
                                    EquipId = pair.Value.EquipId,
                                    ResonanceDict = pair.Value.ResonanceDict?
                                        .OrderBy(resonance => resonance.Key)
                                        .ToDictionary(resonance => resonance.Key, resonance => resonance.Value),
                                    WeaponOverrunSuitId = pair.Value.WeaponOverrunSuitId
                                })
                            ?? []
                    };
            }

            normalized = new TeamPrefabData
            {
                TeamType = TeamKind.Prefab,
                TeamId = source.TeamId,
                CaptainPos = source.CaptainPos,
                FirstFightPos = source.FirstFightPos,
                EnterCgIndex = source.EnterCgIndex,
                SettleCgIndex = source.SettleCgIndex,
                TeamData = teamDataByPosition,
                TeamName = source.TeamName,
                SelectedGeneralSkill = source.SelectedGeneralSkill,
                PartnerData = partnerDataByPosition,
                EquipData = equipDataByPosition,
                TagsSet = tags,
                SwitchSkills = switchSkills
            };
            return true;
        }

        private static bool IsValidCharacterSwitchSkill(CharacterData character, int skillId)
        {
            if (!CharacterSkillRowsById.Value.TryGetValue((int)character.Id, out CharacterSkillTable? skillRow))
                return false;

            return skillRow.SkillGroupId
                .Where(CharacterSkillGroupRowsById.Value.ContainsKey)
                .Select(groupId => CharacterSkillGroupRowsById.Value[groupId])
                .Any(group => group.SkillId.Count > 1 && group.SkillId.Contains(skillId));
        }

        private static bool IsValidGeneralSkill(
            int generalSkillId,
            IEnumerable<CharacterData> characters)
        {
            if (generalSkillId == 0)
                return true;
            if (!CharacterGeneralSkillRowsById.Value.TryGetValue(
                    generalSkillId,
                    out CharacterGeneralSkillTable? generalSkill))
            {
                return false;
            }
            if (generalSkill.IsSkipSkillCheck > 0)
                return true;

            foreach (CharacterData character in characters)
            {
                if (MeetsGeneralSkillRequirement(
                        character.SkillList,
                        generalSkill.SkillId,
                        generalSkill.SkillLevel)
                    || MeetsGeneralSkillRequirement(
                        character.EnhanceSkillList,
                        generalSkill.EnhanceSkillId,
                        generalSkill.EnhanceSkillLevel))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool MeetsGeneralSkillRequirement(
            IReadOnlyList<CharacterSkill>? characterSkills,
            IReadOnlyList<int> requiredSkillIds,
            IReadOnlyList<int> requiredLevels)
        {
            if (characterSkills is null)
                return false;

            for (int index = 0; index < requiredSkillIds.Count; index++)
            {
                int requiredSkillId = requiredSkillIds[index];
                int requiredLevel = requiredLevels.ElementAtOrDefault(index);
                if (characterSkills.Any(skill =>
                        skill.Id == (uint)requiredSkillId && skill.Level >= requiredLevel))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsValidTeamPrefabEquipEntry(
            int characterId,
            int slot,
            EquipData ownedEquip,
            EquipTable equipRow,
            TeamPrefabEquipEntry presetEquip)
        {
            if (equipRow.Site != slot)
                return false;

            if (slot != 0)
            {
                return (presetEquip.ResonanceDict is null || presetEquip.ResonanceDict.Count == 0)
                    && presetEquip.WeaponOverrunSuitId == 0;
            }

            if (presetEquip.WeaponOverrunSuitId < 0
                || (presetEquip.WeaponOverrunSuitId > 0
                    && !(ownedEquip.WeaponOverrunData?.ActiveSuits?.Contains(
                        presetEquip.WeaponOverrunSuitId) ?? false)))
            {
                return false;
            }

            IReadOnlyDictionary<int, int>? resonanceSkills = presetEquip.ResonanceDict;
            if (resonanceSkills is null || resonanceSkills.Count == 0)
                return true;
            if (resonanceSkills.Values.Any(skillId => skillId <= 0)
                || !EquipResonanceRowsById.Value.TryGetValue(
                    (int)ownedEquip.TemplateId,
                    out EquipResonanceTable? resonanceRow))
            {
                return false;
            }

            HashSet<(int CharacterId, int SkillId)> selectedSkillsByCharacter = [];
            foreach ((int resonanceSlot, int skillId) in resonanceSkills)
            {
                int poolId = resonanceSlot > 0
                    ? resonanceRow.WeaponSkillPoolId.ElementAtOrDefault(resonanceSlot - 1)
                    : 0;
                ResonanceInfo? ownedResonance = (ownedEquip.ResonanceInfo ?? [])
                    .Concat(ownedEquip.UnconfirmedResonanceInfo ?? [])
                    .LastOrDefault(value => value.Slot == resonanceSlot);
                int resonanceCharacterId = ownedResonance?.CharacterId > 0
                    ? ownedResonance.CharacterId
                    : characterId;
                bool isConfiguredSkill = poolId > 0
                    && WeaponSkillIdsByPoolAndCharacter.Value.TryGetValue(
                        (poolId, resonanceCharacterId),
                        out HashSet<int>? allowedSkillIds)
                    && allowedSkillIds.Contains(skillId);
                if (ownedResonance is null
                    || !selectedSkillsByCharacter.Add((resonanceCharacterId, skillId))
                    || (ownedResonance.TemplateId != skillId && !isConfiguredSkill))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsValidPartnerSkillData(
            PartnerData partner,
            IReadOnlyDictionary<int, List<int>>? skillData,
            out TeamPrefabValidationFailure failure)
        {
            failure = TeamPrefabValidationFailure.None;
            if (skillData is null)
            {
                failure = TeamPrefabValidationFailure.MissingPartnerSkillData;
                return false;
            }
            if (skillData.Keys.Any(type => type is not (1 or 2)))
            {
                failure = TeamPrefabValidationFailure.InvalidPartnerSkillTypes;
                return false;
            }
            if (!skillData.TryGetValue(1, out List<int>? mainSkills)
                || mainSkills is null
                || mainSkills.Count != 1
                || mainSkills[0] <= 0)
            {
                failure = TeamPrefabValidationFailure.InvalidPartnerMainSkillCount;
                return false;
            }
            if (partner.SkillList is null
                || !partner.SkillList.Any(skill => skill.Type == 1)
                || !PartnerSkillRowsById.Value.TryGetValue(
                    partner.TemplateId,
                    out PartnerSkillTable? skillRow))
            {
                failure = TeamPrefabValidationFailure.UnknownPartnerSkillConfig;
                return false;
            }
            if (!PartnerQualityRowsByIdAndQuality.Value.TryGetValue(
                    (partner.TemplateId, partner.Quality),
                    out PartnerQualityTable? qualityRow))
            {
                failure = TeamPrefabValidationFailure.UnknownPartnerQuality;
                return false;
            }

            HashSet<int> unlockedGroups = (partner.UnlockSkillGroup ?? []).ToHashSet();
            HashSet<int> availableMainSkillIds = new();
            foreach (int groupId in skillRow.MainSkillGroupId.Where(unlockedGroups.Contains))
            {
                if (!PartnerMainSkillGroupRowsById.Value.TryGetValue(
                        groupId,
                        out PartnerMainSkillGroupTable? group))
                {
                    continue;
                }

                int defaultMainSkillId = group.SkillId.FirstOrDefault();
                if (defaultMainSkillId > 0)
                    availableMainSkillIds.Add(defaultMainSkillId);
            }
            if (!availableMainSkillIds.Contains(mainSkills[0]))
            {
                failure = TeamPrefabValidationFailure.LockedPartnerMainSkill;
                return false;
            }

            List<int> passiveSkills = [];
            if (skillData.TryGetValue(2, out List<int>? requestedPassiveSkills)
                && requestedPassiveSkills is not null)
            {
                passiveSkills = requestedPassiveSkills;
            }
            if (qualityRow.SkillColumnCount < 0)
            {
                failure = TeamPrefabValidationFailure.InvalidPartnerPassiveLimit;
                return false;
            }
            if (passiveSkills.Count > qualityRow.SkillColumnCount)
            {
                failure = TeamPrefabValidationFailure.TooManyPartnerPassiveSkills;
                return false;
            }
            if (passiveSkills.Count != passiveSkills.Distinct().Count())
            {
                failure = TeamPrefabValidationFailure.DuplicatePartnerPassiveSkills;
                return false;
            }

            HashSet<int> configuredPassiveSkillIds = new();
            foreach (int groupId in skillRow.PassiveSkillGroupId)
            {
                if (PartnerPassiveSkillGroupRowsById.Value.TryGetValue(
                        groupId,
                        out PartnerPassiveSkillGroupTable? group)
                    && group.SkillId > 0)
                {
                    configuredPassiveSkillIds.Add(group.SkillId);
                }
            }
            HashSet<int> ownedPassiveSkillIds = partner.SkillList
                .Where(skill => skill.Type == 2)
                .Select(skill => skill.Id)
                .ToHashSet();
            if (passiveSkills.Any(skillId =>
                    !configuredPassiveSkillIds.Contains(skillId)
                    || !ownedPassiveSkillIds.Contains(skillId)))
            {
                failure = TeamPrefabValidationFailure.InvalidPartnerPassiveSkill;
                return false;
            }

            return true;
        }

        private static void SendInvalidTeamPrefabResponse(Session session, int packetId)
        {
            session.SendResponse(
                new TeamPrefabSetTeamResponse { Code = TeamManagerSetTeamParaError },
                packetId);
        }

        [RequestPacketHandler("EnterChallengeRequest")]
        public static void HandleEnterChallengeRequestHandler(Session session, Packet.Request packet)
        {
            session.SendResponse(new EnterChallengeResponse(), packet.Id);
        }

        private static bool TryRecoverFailedFightSettle(Session session, Packet.Request packet, out FightSettleRequest request)
        {
            request = null!;
            try
            {
                FightSettleHeaderResult? header = MessagePackSerializer
                    .Deserialize<FightSettleHeaderRequest>(packet.Content).Result;
                if (header is null || header.IsWin && !header.IsForceExit)
                    return false;

                FightSettleResult result = new()
                {
                    IsWin = false,
                    IsForceExit = header.IsForceExit,
                    StageId = header.StageId,
                    FightId = header.FightId,
                    LeftTime = header.LeftTime
                };
                if (!IsAuthorizedFightSettle(session, result))
                    return false;

                request = new FightSettleRequest { Result = result };
                return true;
            }
            catch (MessagePackSerializationException)
            {
                return false;
            }
        }

        private static void ClearFailedFightSettle(Session session)
        {
            BossModule.CancelFight(session);
            session.PendingBossInshotFight = null;
            session.fight = null;
        }

        [RequestPacketHandler("FightSettleRequest")]
        public static void FightSettleRequestHandler(Session session, Packet.Request packet)
        {
            FightSettleRequest? req;
            try
            {
                req = MessagePackSerializer.Deserialize<FightSettleRequest>(packet.Content);
            }
            catch (MessagePackSerializationException)
            {
                if (TryRecoverFailedFightSettle(session, packet, out req))
                {
                    uint stageId = ResolveFightSettleStageId(session, req);
                    session.log.Warn($"Recovered failed fight settlement with malformed optional telemetry for stage {stageId}.");
                    ClearFailedFightSettle(session);
                    session.SendResponse(BuildFailedFightSettleResponse(stageId, req), packet.Id);
                    return;
                }
                ClearFailedFightSettle(session);
                session.SendResponse(new FightSettleResponse { Code = FightAuthorizationError }, packet.Id);
                return;
            }
            if (req?.Result is null)
            {
                ClearFailedFightSettle(session);
                session.SendResponse(new FightSettleResponse { Code = FightAuthorizationError }, packet.Id);
                return;
            }
            int fashionCode = 0;
            bool isFashionStage = req.Result.StageId <= int.MaxValue
                && FashionStoryModule.TryValidateStage(
                    session,
                    (int)req.Result.StageId,
                    out fashionCode);
            if (isFashionStage
                && (fashionCode != 0
                    || session.fight?.PreFight.PreFightData.StageId != req.Result.StageId))
            {
                session.SendResponse(new FightSettleResponse
                {
                    Code = fashionCode != 0 ? fashionCode : FashionStoryModule.PreFightStageNotFound
                }, packet.Id);
                return;
            }
            if (!IsAuthorizedFightSettle(session, req.Result))
            {
                session.SendResponse(new FightSettleResponse { Code = FightAuthorizationError }, packet.Id);
                return;
            }
            StageTable? stageTable = ResolveStageTable(req.Result.StageId, out _);
            if (stageTable is null
                && !BossModule.IsStage(req.Result.StageId)
                && !BossInshotModule.IsStage(req.Result.StageId)
                && !(ArenaModule.IsArenaStage(req.Result.StageId)
                    && session.fight?.PreFight.PreFightData.SelectAreaId > 0)
                && !RepeatChallengeModule.IsStage(req.Result.StageId)
                && !MainLineLuosaitaPayloadFactory.HasCapturedStageProgress((int)req.Result.StageId))
            {
                session.log.Warn($"[STAGE-PROBE] FightSettleStageTableMissing stageId={req.Result.StageId} fightId={req.Result.FightId}");
            }

            StageLevelControlTable? levelControl = ResolveStageLevelControl(
                req.Result.StageId,
                (int)session.player.PlayerData.Level);
            int challengeCount = session.fight?.PreFight.PreFightData.ChallengeCount ?? 1;
            uint responseStageId = ResolveFightSettleStageId(session, req);
            StageDatum? previousStageData = session.stage?.Stages.TryGetValue(responseStageId, out StageDatum? existingStageData) == true ? existingStageData : null;
            bool isQuickClear = responseStageId != req.Result.StageId;
            bool isFirstClear = previousStageData is null;
            bool isSuccessfulSettle = req.Result.IsWin && !req.Result.IsForceExit;
            if (TransfiniteModule.TrySettle(session, req.Result, out FightSettleResponse transfiniteResponse))
            {
                session.fight = null;
                session.SendResponse(transfiniteResponse, packet.Id);
                return;
            }
            if (!req.Result.IsForceExit
                && BossModule.TryBuildFightSettle(session, req.Result, out BossSingleFightResult? bossSingleResult))
            {
                FightSettleResponse response = new()
                {
                    Code = 0,
                    Settle = new()
                    {
                        IsWin = true,
                        StageId = req.Result.StageId,
                        LeftTime = checked((int)req.Result.LeftTime),
                        NpcHpInfo = req.Result.NpcHpInfo,
                        // Pain Cage attempts are committed by BossSingleSaveScoreRequest, not generic settlement.
                        ChallengeCount = 0,
                        BossSingleFightResult = bossSingleResult
                    }
                };
                session.fight = null;
                session.SendResponse(response, packet.Id);
                return;
            }
            if (!isSuccessfulSettle
                && !req.Result.IsForceExit
                && ArenaModule.IsArenaStage(req.Result.StageId)
                && session.fight?.PreFight.PreFightData.SelectAreaId > 0
                && ArenaModule.RecordFightResult(session, req.Result) is ArenaResult arenaDeathResult)
            {
                TaskModule.RecordArenaResult(session, arenaDeathResult.Point);
                session.fight = null;
                session.SendResponse(new FightSettleResponse
                {
                    Code = 0,
                    Settle = new()
                    {
                        IsWin = true,
                        StageId = req.Result.StageId,
                        LeftTime = checked((int)req.Result.LeftTime),
                        NpcHpInfo = req.Result.NpcHpInfo,
                        RewardGoodsList = null!,
                        MultiRewardGoodsList = null!,
                        ChallengeCount = 0,
                        ArenaResult = arenaDeathResult
                    }
                }, packet.Id);
                return;
            }

            if (!isSuccessfulSettle)
            {
                BiancaTheatreModule.TrySendTheatreRetreatSettle(session, req.Result.StageId);
                ClearFailedFightSettle(session);
                session.SendResponse(BuildFailedFightSettleResponse(responseStageId, req), packet.Id);
                return;
            }
            if (BossInshotModule.TrySettle(session, req.Result, out FightSettleResponse bossInshotResponse))
            {
                if (session.PendingBossInshotFight is null)
                    session.fight = null;
                if (bossInshotResponse.Code == 0)
                {
                    session.SendPush(BossInshotModule.BuildNotifyBossInshotData(session.player));
                    session.SendPush(new NotifyArchiveMonsterRecord());
                    TaskModule.SendTaskSync(session);
                }
                session.SendResponse(bossInshotResponse, packet.Id);
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
            List<RewardApplicationResult>? deferredRewardApplications = null;
            bool deferFashionFirstClearPushes = isFashionStage
                && isFirstClear
                && FashionStoryModule.IsTrialStage(session, (int)req.Result.StageId);
            if (deferFashionFirstClearPushes)
                deferredRewardApplications = [];
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

                if (deferredRewardApplications is not null)
                {
                    RewardApplicationResult application = RewardHandler.ApplyRewards(rewardGoods, session);
                    deferredRewardApplications.Add(application);
                    multiRewards.Add(new List<RewardGoods>(application.RewardGoods));
                }
                else
                {
                    List<RewardGoods> rewards = RewardHandler.GiveRewards(rewardGoods, session);
                    multiRewards.Add(new List<RewardGoods>(rewards));
                }
            }

            if (notifyItemData.ItemDataList.Count > 0 && !deferFashionFirstClearPushes)
            {
                session.SendPush(notifyItemData);
            }
            session.ExpSanityCheck();
            NotifyCharacterDataList? deferredCharacterData = null;

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
                
                if (!deferFashionFirstClearPushes)
                    session.SendPush(charData);
                if (deferFashionFirstClearPushes)
                    deferredCharacterData = charData;
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

            ArenaResult? arenaResult = ArenaModule.IsArenaStage(req.Result.StageId)
                && session.fight?.PreFight.PreFightData.SelectAreaId > 0
                    ? ArenaModule.RecordFightResult(session, req.Result)
                    : null;
            if (arenaResult is not null)
            {
                TaskModule.RecordArenaResult(session, arenaResult.Point);
            }

            bool updatedRepeatChallenge = RepeatChallengeModule.RecordStageClear(session.player, req.Result.StageId, challengeCount);
            session.player.Save();
            session.inventory.Save();
            session.character.Save();
            session.stage.Save();
            if (deferredRewardApplications is not null)
            {
                foreach (RewardApplicationResult application in deferredRewardApplications)
                    application.SendPushes(session);
                if (notifyItemData.ItemDataList.Count > 0)
                    session.SendPush(notifyItemData);
                if (deferredCharacterData is not null
                    && deferredCharacterData.CharacterDataList.Count > 0)
                {
                    session.SendPush(deferredCharacterData);
                }
            }

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
                    ChallengeCount = isQuickClear ? 0 : challengeCount,
                    ArenaResult = arenaResult,
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
            if (updatedRepeatChallenge)
                session.SendPush(RepeatChallengeModule.BuildExpChange(session.player));
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

        private static bool IsAuthorizedFightSettle(Session session, FightSettleResult result)
        {
            return session.fight is { } fight
                && result.FightId >= int.MinValue
                && result.FightId <= uint.MaxValue
                && fight.FightId == unchecked((uint)result.FightId)
                && fight.PreFight.PreFightData.StageId == result.StageId;
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
