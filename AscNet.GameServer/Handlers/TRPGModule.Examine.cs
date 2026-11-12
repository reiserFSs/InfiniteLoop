using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.trpg;
using AscNet.Table.V2.share.trpg.examine;
using MessagePack;

namespace AscNet.GameServer.Handlers
{
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    [MessagePackObject(true)]
    public class TRPGExamineRequest
    {
        public int Id { get; set; }
        public int ActionId { get; set; }
    }

    [MessagePackObject(true)]
    public class TRPGExamineResponse
    {
        public int Code { get; set; }
    }

    [MessagePackObject(true)]
    public class TRPGExamineCharacterRequest
    {
        public int Id { get; set; }
        public int ActionId { get; set; }
        public int Round { get; set; }
        public int CharacterId { get; set; }
        public int? UseItemId { get; set; }
    }

    [MessagePackObject(true)]
    public class TRPGExamineCharacterResponse
    {
        public int Code { get; set; }
        public int Score { get; set; }
    }

    [MessagePackObject(true)]
    public class TRPGExamineResultRequest
    {
        public int Id { get; set; }
    }

    /// <summary>XTRPGExamine:UpdateResult reads Id, Success and PunishId.</summary>
    [MessagePackObject(true)]
    public class TRPGExamineResultResponse
    {
        public int Code { get; set; }
        public int Id { get; set; }
        public bool Success { get; set; }
        public int PunishId { get; set; }
    }

    [MessagePackObject(true)]
    public class TRPGExamineCharacterResetRequest
    {
        public int CharacterId { get; set; }
    }

    [MessagePackObject(true)]
    public class TRPGExamineCharacterResetResponse
    {
        public int Code { get; set; }
    }

    [MessagePackObject(true)]
    public class TRPGExamineChangeRoundRequest
    {
    }

    [MessagePackObject(true)]
    public class TRPGExamineChangeRoundResponse
    {
        public int Code { get; set; }
    }
#pragma warning restore CS8618

    internal partial class TRPGModule
    {
        private const int ExamineIdError = 20104039, ExamineActionIdError = 20104040, EnduranceNotEnough = 20104041,
            ExamineInstNotExist = 20104042, ExamineReachMaxRound = 20104043, ExamineRoundNotFinish = 20104044,
            ExamineIdMismatch = 20104045, ExamineAttributeNotExist = 20104046, ExamineRoundError = 20104047,
            ExamineCharacterAlreadyExamine = 20104048, ExamineUseItemNotExist = 20104049, ExamineUseItemEffectError = 20104050,
            ExamineUseItemAttributeError = 20104051, ExamineCharacterNoScore = 20104052, FuncExamineNotFinish = 20104013;
        // XTRPGConfigs.TRPGExamineActionType: 1-3 roll Combat/Perception/Arithmetic, 4 spends an item. ItemEffect 3 = AddExamineAttribute.
        private const int ActionConsumeItem = 4, ItemEffectAddExamineAttribute = 3, DefaultEffectItemId = -1;
        private const int PunishDeBuff = 2, PunishLoseItem = 3, PunishGoToOrigin = 4;
        private const int TalentExamineReward = 3, TalentPunishReduce = 5;

        private static List<TRPGExamineTable> Examines => TableReaderV2.Parse<TRPGExamineTable>();
        private static List<TRPGExamineActionTable> ExamineActions => TableReaderV2.Parse<TRPGExamineActionTable>();

        internal static bool ExaminePassed(Player player, int examineId) => player.Trpg.ExaminesPassed.Contains(examineId);

        private static int RolledTotal(TrpgExamineState inst) => inst.Carried + inst.Scores.Sum(x => x.Score);

        [RequestPacketHandler("TRPGExamineRequest")]
        public static void TRPGExamineRequestHandler(Session session, Packet.Request packet)
        {
            TRPGExamineRequest request = packet.Deserialize<TRPGExamineRequest>();
            TRPGExamineResponse response = new();
            TrpgState state = session.player.Trpg;
            TRPGExamineTable? examine = Examines.Find(x => x.Id == request.Id);
            TRPGExamineActionTable? action = ExamineActions.Find(x => x.Id == request.ActionId);
            List<TRPGRoleTable> owned = TrpgTalents.OwnedRoles(session.player);
            TrpgExplore.RefreshEndurance(session);

            if (examine is null) response.Code = ExamineIdError;
            else if (action is null || !examine.ActionId.Contains(action.Id)) response.Code = ExamineActionIdError;
            else if (TrpgMaze.CheckExamineCard(session, examine.Id) is int card and not 0) response.Code = card;
            else if (action.Type != ActionConsumeItem && owned.Count == 0) response.Code = CharacterNotOwner;
            else if (state.Endurance < examine.Endurance) response.Code = EnduranceNotEnough;
            else if (action.Type == ActionConsumeItem && TrpgItems.Count(session, action.ItemId) < 1) response.Code = UseItemCountNotEnough;
            else
            {
                // AscNet rule: a new check replaces an unfinished one (the client starts fresh after a relog; the endurance is already spent).
                state.Examine = new TrpgExamineState
                {
                    ExamineId = examine.Id,
                    ActionId = action.Id,
                    Round = 1,
                    Scores = action.Type == ActionConsumeItem ? new() : owned.Select(x => new TrpgRoleScore { RoleId = x.Id }).ToList()
                };
                session.player.SaveChecked();
                TrpgExplore.TryConsumeEndurance(session, examine.Endurance);
                if (action.Type == ActionConsumeItem) TrpgItems.TryConsume(session, action.ItemId, 1);
            }

            session.SendResponse(response, packet.Id);
        }

        [RequestPacketHandler("TRPGExamineCharacterRequest")]
        public static void TRPGExamineCharacterRequestHandler(Session session, Packet.Request packet)
        {
            TRPGExamineCharacterRequest request = packet.Deserialize<TRPGExamineCharacterRequest>();
            TRPGExamineCharacterResponse response = new();
            TrpgExamineState? inst = session.player.Trpg.Examine;
            TRPGExamineActionTable? action = inst is null ? null : ExamineActions.Find(x => x.Id == inst.ActionId);
            TRPGRoleTable? role = TrpgTalents.Roles.Find(x => x.Id == request.CharacterId);
            TrpgRoleScore? slot = inst?.Scores.Find(x => x.RoleId == request.CharacterId);
            int? useItem = request.UseItemId is null or DefaultEffectItemId ? null : request.UseItemId;
            TRPGItemTable? item = useItem is { } id ? TrpgItems.Config(id) : null;
            int bonus = 0;

            if (inst is null || action is null) response.Code = ExamineInstNotExist;
            else if (request.Id != inst.ExamineId) response.Code = ExamineIdMismatch;
            else if (request.ActionId != inst.ActionId) response.Code = ExamineActionIdError;
            else if (action.Type == ActionConsumeItem) response.Code = ExamineAttributeNotExist;
            else if (request.Round != inst.Round) response.Code = ExamineRoundError;
            else if (role is null || slot is null) response.Code = CharacterNotOwner;
            else if (slot.Score != 0) response.Code = ExamineCharacterAlreadyExamine;
            else if (useItem is not null && (item is null || TrpgItems.Count(session, item.Id) < 1)) response.Code = ExamineUseItemNotExist;
            else if (item is not null && item.EffectType != ItemEffectAddExamineAttribute) response.Code = ExamineUseItemEffectError;
            else if (item is not null && item.Params.FirstOrDefault() != action.Type) response.Code = ExamineUseItemAttributeError;
            else
            {
                if (item is not null) bonus = item.Params.ElementAtOrDefault(1);
                TRPGCharacterAttributeData attribute = TrpgTalents.Character(session, role).Attributes.First(x => x.Id == action.Type);
                // AscNet roll rule: uniform in the [MinRollValue, MaxRollValue] the investigator reports, plus the item's attribute bonus.
                slot.Score = Random.Shared.Next(attribute.MinRollValue, attribute.MaxRollValue + 1) + bonus;
                session.player.SaveChecked();
                if (item is not null) TrpgItems.TryConsume(session, item.Id, 1);
                response.Score = slot.Score;
            }

            session.SendResponse(response, packet.Id);
        }

        [RequestPacketHandler("TRPGExamineCharacterResetRequest")]
        public static void TRPGExamineCharacterResetRequestHandler(Session session, Packet.Request packet)
        {
            TRPGExamineCharacterResetRequest request = packet.Deserialize<TRPGExamineCharacterResetRequest>();
            TRPGExamineCharacterResetResponse response = new();
            TrpgExamineState? inst = session.player.Trpg.Examine;
            TRPGExamineActionTable? action = inst is null ? null : ExamineActions.Find(x => x.Id == inst.ActionId);
            TrpgRoleScore? slot = inst?.Scores.Find(x => x.RoleId == request.CharacterId);

            if (inst is null || action is null) response.Code = ExamineInstNotExist;
            else if (slot is null) response.Code = CharacterNotOwner;
            else if (slot.Score == 0) response.Code = ExamineCharacterNoScore;
            else if (TrpgItems.Count(session, action.ResetCostId) < action.ResetCostCount) response.Code = UseItemCountNotEnough;
            else
            {
                slot.Score = 0;
                session.player.SaveChecked();
                TrpgItems.TryConsume(session, action.ResetCostId, action.ResetCostCount);
            }

            session.SendResponse(response, packet.Id);
        }

        [RequestPacketHandler("TRPGExamineChangeRoundRequest")]
        public static void TRPGExamineChangeRoundRequestHandler(Session session, Packet.Request packet)
        {
            TRPGExamineChangeRoundResponse response = new();
            TrpgExamineState? inst = session.player.Trpg.Examine;
            TRPGExamineActionTable? action = inst is null ? null : ExamineActions.Find(x => x.Id == inst.ActionId);

            if (inst is null || action is null) response.Code = ExamineInstNotExist;
            else if (inst.Round >= action.Round) response.Code = ExamineReachMaxRound;
            else if (inst.Scores.Any(x => x.Score == 0)) response.Code = ExamineRoundNotFinish;
            else
            {
                inst.Carried += inst.Scores.Sum(x => x.Score);
                inst.Scores.ForEach(x => x.Score = 0);
                inst.Round++;
                session.player.SaveChecked();
            }

            session.SendResponse(response, packet.Id);
        }

        [RequestPacketHandler("TRPGExamineResultRequest")]
        public static void TRPGExamineResultRequestHandler(Session session, Packet.Request packet)
        {
            TRPGExamineResultRequest request = packet.Deserialize<TRPGExamineResultRequest>();
            TRPGExamineResultResponse response = new();
            TrpgState state = session.player.Trpg;
            TrpgExamineState? inst = state.Examine;
            TRPGExamineActionTable? action = inst is null ? null : ExamineActions.Find(x => x.Id == inst.ActionId);
            TRPGExamineTable? examine = inst is null ? null : Examines.Find(x => x.Id == inst.ExamineId);

            if (inst is null || action is null || examine is null) response.Code = ExamineInstNotExist;
            else if (request.Id != inst.ExamineId) response.Code = ExamineIdMismatch;
            else
            {
                // The item check passes outright (XTRPGExamine:Start); roll checks need the summed score to reach NeedValue.
                response.Id = examine.Id;
                response.Success = action.Type == ActionConsumeItem || RolledTotal(inst) >= action.NeedValue;
                state.Examine = null;
                if (response.Success)
                {
                    if (!state.ExaminesPassed.Contains(examine.Id)) state.ExaminesPassed.Add(examine.Id);
                    GrantExamineTalentRewards(session);
                }
                else response.PunishId = ApplyPunish(session, examine);
                session.player.SaveChecked();
                TrpgMaze.OnExamineFinished(session, examine.Id, response.Success);
            }

            session.SendResponse(response, packet.Id);
        }

        /// <summary>Talent TRPGBuff Type 3 [rewardId]: "complete a check event to get a bonus reward".</summary>
        private static void GrantExamineTalentRewards(Session session)
        {
            foreach (var buff in TrpgTalents.ActiveBuffs(session).Where(x => x.Type == TalentExamineReward))
            {
                List<AscNet.Table.V2.share.reward.RewardGoodsTable> goods = RewardHandler.GetRewardGoods(buff.Params.First());
                if (goods.Count == 0) continue;
                // Repeatable grant: the claim key needs a per-grant counter (saved with the player by the caller).
                RewardApplicationResult granted = RewardHandler.ApplyRewardsOnceAndPersist([new RewardGrant($"trpg:examine-talent:{++session.player.Trpg.RewardSeq}", goods)], session);
                granted.SendPushes(session);
            }
        }

        /// <summary>Picks the failure punishment (PunishWeight over PunishId; id 0 = none) and applies its server-side part.
        /// Returns the id the client acts on (fight stage / return to origin are client-driven).</summary>
        private static int ApplyPunish(Session session, TRPGExamineTable examine)
        {
            int total = examine.PunishWeight.Sum();
            if (total <= 0 || examine.PunishId.Count == 0) return 0;
            // AscNet rule: TRPGBuff Type 5 [percent] ("lower chance to encounter a negative event after failing") skips the punishment with that chance, summed over talents.
            int skip = TrpgTalents.ActiveBuffs(session).Where(x => x.Type == TalentPunishReduce).Sum(x => x.Params.FirstOrDefault());
            if (Random.Shared.Next(100) < skip) return 0;

            int roll = Random.Shared.Next(total), index = 0;
            while (index < examine.PunishWeight.Count - 1 && roll >= examine.PunishWeight[index]) roll -= examine.PunishWeight[index++];
            int punishId = examine.PunishId[index];
            TRPGExaminePunishTable? punish = TableReaderV2.Parse<TRPGExaminePunishTable>().Find(x => x.Id == punishId);
            if (punish is null) return 0;

            if (punish.Type == PunishDeBuff)
            {
                List<TRPGRoleTable> owned = TrpgTalents.OwnedRoles(session.player);
                if (owned.Count > 0)
                {
                    TRPGRoleTable victim = owned[Random.Shared.Next(owned.Count)];
                    TrpgRoleState victimState = TrpgTalents.RoleState(session.player, victim.Id);
                    victimState.Buffs.RemoveAll(x => x.Period != TrpgTalents.Period || x.Id == punish.Params.First());
                    victimState.Buffs.Add(new TrpgBuffState { Id = punish.Params.First(), Period = TrpgTalents.Period });
                    session.player.SaveChecked();
                    TrpgTalents.PushCharacters(session, [victim]);
                }
            }
            else if (punish.Type == PunishLoseItem)
            {
                // Lose up to the stated amount; the bag cannot go negative.
                int lost = (int)Math.Min(punish.Params.ElementAtOrDefault(1), TrpgItems.Count(session, punish.Params.First()));
                if (lost > 0) TrpgItems.TryConsume(session, punish.Params.First(), lost);
            }
            else if (punish.Type == PunishGoToOrigin) TrpgMaze.RestartCurrentLayer(session);
            return punishId;
        }
    }
}
