using AscNet.Common.MsgPack;
using MessagePack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.guide;
using AscNet.Table.V2.share.condition;
using AscNet.Table.V2.share.equip;

namespace AscNet.GameServer.Handlers
{
    #region MsgPackScheme
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    [MessagePackObject(true)]
    public class GuideGroupFinishRequest
    {
        public int GroupId;
    }

    [MessagePackObject(true)]
    public class GuideGroupFinishResponse
    {
        public int Code;
        public List<dynamic>? RewardGoodsList;
    }

    [MessagePackObject(true)]
    public class GuideCompleteRequest
    {
        public int GuideGroupId;
    }

    [MessagePackObject(true)]
    public class NotifyGuide
    {
        public int GuideGroupId;
    }

    [MessagePackObject(true)]
    public class GuideCompleteResponse
    {
        public int Code;
        public List<RewardGoods>? RewardGoodsList;
    }
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    #endregion

    internal class GuideModule
    {
        private static readonly Lazy<Dictionary<int, GuideGroupTable>> GuideGroups = new(() =>
            TableReaderV2.Parse<GuideGroupTable>().ToDictionary(guide => guide.Id));
        private static readonly Lazy<Dictionary<int, ConditionTable>> Conditions = new(() =>
            TableReaderV2.Parse<ConditionTable>().ToDictionary(condition => condition.Id));
        private static readonly Lazy<HashSet<(long WeaponId, int CharacterId)>> HarmonyTwoWeapons = new(() =>
            TableReaderV2.Parse<WeaponOverrunTable>()
                .Where(row => row.Level == 2 && row.CharacterId is > 0)
                .Select(row => ((long)row.WeaponId, row.CharacterId!.Value))
                .ToHashSet());
        private static bool HasEquippedHarmonyTwoWeapon(Session session)
            => session.character.Equips.Any(equip =>
                equip.CharacterId > 0
                && HarmonyTwoWeapons.Value.Contains(((long)equip.TemplateId, equip.CharacterId)));
        [RequestPacketHandler("GuideOpenRequest")]
        public static void GuideOpenRequestHandler(Session session, Packet.Request packet)
        {
            GuideCompleteRequest request = packet.Deserialize<GuideCompleteRequest>();
            session.SendResponse(new GuideOpenResponse
            {
                Code = IsValidGuide(request.GuideGroupId) ? 0 : 1
            }, packet.Id);
        }

        // TODO: Invalid, need proper types
        [RequestPacketHandler("GuideGroupFinishRequest")]
        public static void GuideGroupFinishRequestHandler(Session session, Packet.Request packet)
        {
            session.SendResponse(new GuideGroupFinishResponse(), packet.Id);
        }

        [RequestPacketHandler("GuideCompleteRequest")]
        public static void GuideCompleteRequestHandler(Session session, Packet.Request packet)
        {
            GuideCompleteRequest request = MessagePackSerializer.Deserialize<GuideCompleteRequest>(packet.Content);
            if (!GuideGroups.Value.TryGetValue(request.GuideGroupId, out GuideGroupTable? guide)
                || guide.CompleteId != request.GuideGroupId)
            {
                session.SendResponse(new GuideCompleteResponse { Code = 1 }, packet.Id);
                return;
            }

            session.player.PlayerData.GuideData ??= new();
            if (session.player.PlayerData.GuideData.Contains(request.GuideGroupId))
            {
                session.SendResponse(new GuideCompleteResponse(), packet.Id);
                return;
            }

            string claimKey = $"guide:{request.GuideGroupId}";
            bool rewardAlreadyApplied = session.inventory.AppliedRewardClaims.Contains(
                    claimKey,
                    StringComparer.Ordinal)
                || session.character.AppliedRewardClaims.Contains(
                    claimKey,
                    StringComparer.Ordinal);
            int failedConditionId = rewardAlreadyApplied
                ? 0
                : guide.ConditionId.FirstOrDefault(conditionId => !ConditionSatisfied(session, conditionId));
            if (failedConditionId != 0)
            {
                session.log.Warn(
                    $"Guide completion {request.GuideGroupId} failed condition {failedConditionId}");
                session.SendResponse(new GuideCompleteResponse { Code = 1 }, packet.Id);
                return;
            }




            RewardApplicationResult? rewardApplication = null;
            if (guide.RewardId is > 0)
            {
                var configuredRewards = RewardHandler.GetRewardGoods(guide.RewardId);
                if (configuredRewards.Count == 0)
                {
                    session.SendResponse(new GuideCompleteResponse { Code = 1 }, packet.Id);
                    return;
                }
                try
                {
                    rewardApplication = RewardHandler.ApplyRewardsOnceAndPersist(
                        [new RewardGrant(claimKey, configuredRewards)],
                        session);
                }
                catch (Exception exception)
                {
                    session.log.Error(
                        $"Failed to persist guide reward {request.GuideGroupId}: {exception}");
                    session.SendResponse(new GuideCompleteResponse { Code = 1 }, packet.Id);
                    return;
                }
            }

            session.player.PlayerData.GuideData.Add(request.GuideGroupId);
            try
            {
                session.player.SaveChecked();
            }
            catch (Exception exception)
            {
                session.player.PlayerData.GuideData.Remove(request.GuideGroupId);
                session.log.Error(
                    $"Failed to persist guide completion {request.GuideGroupId}: {exception}");
                session.SendResponse(new GuideCompleteResponse { Code = 1 }, packet.Id);
                return;
            }
            rewardApplication?.SendPushes(session);
            session.SendResponse(new GuideCompleteResponse
            {
                RewardGoodsList = rewardApplication?.RewardGoods
            }, packet.Id);
        }
        private static bool ConditionSatisfied(Session session, int conditionId)
            => ConditionSatisfied(session, conditionId, new HashSet<int>());

        private static bool ConditionSatisfied(Session session, int conditionId, HashSet<int> visiting)
        {
            if (!Conditions.Value.TryGetValue(conditionId, out ConditionTable? condition)
                || !visiting.Add(conditionId))
                return false;

            try
            {
                if (!string.IsNullOrWhiteSpace(condition.Formula))
                {
                    bool isAny = condition.Formula.Contains('|');
                    if (isAny && condition.Formula.Contains('&'))
                        return false;

                    string[] terms = condition.Formula.Split(isAny ? '|' : '&', StringSplitOptions.RemoveEmptyEntries);
                    if (terms.Length == 0)
                        return false;

                    bool Evaluate(string term)
                    {
                        string value = term.Trim();
                        bool negate = value.StartsWith('!');
                        if (negate)
                            value = value[1..];
                        return int.TryParse(value, out int referencedId)
                            && (ConditionSatisfied(session, referencedId, visiting) != negate);
                    }

                    return isAny ? terms.Any(Evaluate) : terms.All(Evaluate);
                }


                return condition.Type switch
                {
                    10101 => condition.Params.Count > 0
                        && condition.Params.All(requiredLevel => session.player.PlayerData.Level >= requiredLevel),
                    10102 => condition.Params.Count > 0
                        && condition.Params.All(characterId =>
                            session.character.Characters.Any(character => character.Id == (uint)characterId)),
                    10105 => condition.Params.Count > 0
                        && condition.Params.All(stageId =>
                            session.stage.Stages.TryGetValue((uint)stageId, out StageDatum? stage) && stage.Passed),
                    10108 => condition.Params.Count > 0
                        && condition.Params.All(stageId =>
                            !session.stage.Stages.TryGetValue((uint)stageId, out StageDatum? stage) || !stage.Passed),
                    10187 => condition.Params.Count >= 2
                        && (session.player.PlayerData.GuideData?.Contains(condition.Params[1]) == true)
                            == (condition.Params[0] != 0),
                    13124 => HasEquippedHarmonyTwoWeapon(session),
                    _ => false
                };
            }
            finally
            {
                visiting.Remove(conditionId);
            }
        }

        private static bool IsValidGuide(int guideGroupId)
        {
            return GuideGroups.Value.TryGetValue(guideGroupId, out GuideGroupTable? guide)
                && guide.CompleteId == guideGroupId;
        }
    }
}
