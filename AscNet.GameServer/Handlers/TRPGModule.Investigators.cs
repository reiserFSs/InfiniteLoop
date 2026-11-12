using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.trpg;
using MessagePack;

namespace AscNet.GameServer.Handlers
{
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    [MessagePackObject(true)]
    public class TRPGCharacterAttributeData
    {
        public int Id { get; set; }
        public int Value { get; set; }
        public int MinRollValue { get; set; }
        public int MaxRollValue { get; set; }
    }

    /// <summary>XTRPGRole:UpdateData reads BuffIds, Attributes{Id,Value,MinRollValue,MaxRollValue} and TalentIds.</summary>
    [MessagePackObject(true)]
    public class TRPGCharacterData
    {
        public int Id { get; set; }
        public List<TRPGCharacterAttributeData> Attributes { get; set; } = new();
        public List<int> TalentIds { get; set; } = new();
        public List<int> BuffIds { get; set; } = new();
    }

    [MessagePackObject(true)]
    public class NotifyTRPGCharacterData
    {
        public List<TRPGCharacterData> Characters { get; set; } = new();
    }

    [MessagePackObject(true)]
    public class TRPGActivateTalentRequest
    {
        public int CharacterId { get; set; }
        public int TalentId { get; set; }
    }

    [MessagePackObject(true)]
    public class TRPGActivateTalentResponse
    {
        public int Code { get; set; }
    }

    [MessagePackObject(true)]
    public class TRPGResetTalentRequest
    {
        public int CharacterId { get; set; }
    }

    [MessagePackObject(true)]
    public class TRPGResetTalentResponse
    {
        public int Code { get; set; }
    }

    [MessagePackObject(true)]
    public class TRPGUseItemRequest
    {
        public int ItemId { get; set; }
        public int ItemCount { get; set; }
        public int CharacterId { get; set; }
    }

    [MessagePackObject(true)]
    public class TRPGUseItemResponse
    {
        public int Code { get; set; }
    }
#pragma warning restore CS8618

    /// <summary>Investigators (TRPGRole), their talents and day-long buffs. An investigator is owned once its UnlockTarget is finished.</summary>
    internal static class TrpgTalents
    {
        // XTRPGConfigs.RoleAttributeType: 1 Combat, 2 Perception, 3 Arithmetic. Client TRPGRoleAttribute.MaxValue is 10 for all three.
        internal const int AttributeCount = 3, AttributeMax = 10, MinRoll = 1;
        // TRPGBuff.EffectType (XTRPGConfigs.TRPGBuffEffectType) and the Type rows whose meaning the buff text states.
        private const int EffectPositive = 2, EffectNegative = 3;
        private const int BuffAttributeAdd = 2, BuffRollFloor = 4, BuffCapacity = 10, BuffAttributeSub = 11;

        internal static List<TRPGRoleTable> Roles => TableReaderV2.Parse<TRPGRoleTable>();
        private static List<TRPGBuffTable> Buffs => TableReaderV2.Parse<TRPGBuffTable>();
        private static List<TRPGRoleTalentGroupTable> Talents => TableReaderV2.Parse<TRPGRoleTalentGroupTable>();

        internal static long Period => TaskModule.CurrentDailyResetPeriod(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        internal static List<TRPGRoleTable> OwnedRoles(Player player) =>
            Roles.Where(x => TRPGModule.TargetFinished(player, x.UnlockTarget)).OrderBy(x => x.Id).ToList();

        internal static TrpgRoleState RoleState(Player player, int roleId)
        {
            TrpgRoleState? state = player.Trpg.Roles.Find(x => x.Id == roleId);
            if (state is null) player.Trpg.Roles.Add(state = new TrpgRoleState { Id = roleId });
            return state;
        }

        /// <summary>Read-only view: a role nobody touched yet has no stored state.</summary>
        private static TrpgRoleState ReadState(Player player, int roleId) => player.Trpg.Roles.Find(x => x.Id == roleId) ?? new TrpgRoleState { Id = roleId };

        internal static TRPGRoleTalentGroupTable? Talent(TRPGRoleTable role, int talentId) =>
            Talents.Find(x => x.GroupId == role.TalentGroupId && x.Id == talentId);

        private static IEnumerable<TRPGBuffTable> TalentBuffs(TrpgRoleState state, TRPGRoleTable role) =>
            state.Talents.Select(id => Talent(role, id)).Where(x => x is not null)
                .Select(x => Buffs.Find(b => b.Id == x!.EventId)).Where(x => x is not null).Select(x => x!);

        /// <summary>Buff rows of every activated talent of every owned investigator.</summary>
        internal static List<TRPGBuffTable> ActiveBuffs(Session session) =>
            OwnedRoles(session.player).SelectMany(role => TalentBuffs(ReadState(session.player, role.Id), role)).ToList();

        internal static int ItemCapacityAdd(Session session) =>
            ActiveBuffs(session).Where(x => x.Type == BuffCapacity).Sum(x => x.Params.FirstOrDefault());

        internal static int UsedPoints(Player player) =>
            player.Trpg.Roles.Sum(state => Roles.Find(x => x.Id == state.Id) is { } role
                ? state.Talents.Sum(id => Talent(role, id)?.CostPoint ?? 0) : 0);

        internal static List<TrpgBuffState> DayBuffs(TrpgRoleState state) => state.Buffs.Where(x => x.Period == Period).ToList();

        internal static TRPGCharacterData Character(Session session, TRPGRoleTable role)
        {
            TrpgRoleState state = ReadState(session.player, role.Id);
            int[] value = new int[AttributeCount];
            for (int i = 0; i < AttributeCount; i++) value[i] = role.InitAttribute.ElementAtOrDefault(i);
            List<TRPGBuffTable> talents = TalentBuffs(state, role).ToList();
            List<TRPGBuffTable> day = DayBuffs(state).Select(x => Buffs.Find(b => b.Id == x.Id)).Where(x => x is not null).Select(x => x!).ToList();
            foreach (TRPGBuffTable buff in talents.Concat(day))
            {
                int sign = buff.Type == BuffAttributeSub ? -1 : buff.Type == BuffAttributeAdd ? 1 : 0;
                for (int i = 0; sign != 0 && i + 1 < buff.Params.Count; i += 2)
                    if (buff.Params[i] is >= 1 and <= AttributeCount) value[buff.Params[i] - 1] += sign * buff.Params[i + 1];
            }

            TRPGCharacterData data = new() { Id = role.Id, TalentIds = state.Talents.ToList(), BuffIds = day.Select(x => x.Id).ToList() };
            for (int attr = 1; attr <= AttributeCount; attr++)
            {
                int max = Math.Clamp(value[attr - 1], MinRoll, AttributeMax);
                // AscNet roll rule (no published law): a roll is uniform in [1, attribute]; a "higher chance to get the max roll" talent
                // (TRPGBuff Type 4 [attr, floorBonus, percent]) raises the floor by floorBonus.
                int min = Math.Min(max, MinRoll + talents.Where(x => x.Type == BuffRollFloor && x.Params.FirstOrDefault() == attr).Sum(x => x.Params.ElementAtOrDefault(1)));
                data.Attributes.Add(new() { Id = attr, Value = max, MinRollValue = min, MaxRollValue = max });
            }
            return data;
        }

        internal static List<TRPGCharacterData> Characters(Session session) =>
            OwnedRoles(session.player).Select(role => Character(session, role)).ToList();

        internal static void PushCharacters(Session session, IEnumerable<TRPGRoleTable> roles) =>
            session.SendPush(new NotifyTRPGCharacterData { Characters = roles.Select(role => Character(session, role)).ToList() });
    }

    internal partial class TRPGModule
    {
        private const int CharacterNotOwner = 20104014, CharacterCfgNotExist = 20104015, TalentCfgNotExist = 20104016,
            TalentAlreadyActivate = 20104017, TalentPreNotActivate = 20104018, TalentPointNotEnough = 20104019,
            CharacterTalentsEmpty = 20104020, ItemCfgNotExist = 20104035, UseItemCountError = 20104036,
            UseItemCountNotEnough = 20104037, ItemCanNotUse = 20104038;
        private const int ItemEffectClearBuff = 1, ItemEffectAddBuff = 2, ItemTypeSpecial = 2, BuffEffectNegative = 3;

        [RequestPacketHandler("TRPGActivateTalentRequest")]
        public static void TRPGActivateTalentRequestHandler(Session session, Packet.Request packet)
        {
            TRPGActivateTalentRequest request = packet.Deserialize<TRPGActivateTalentRequest>();
            TRPGActivateTalentResponse response = new();
            TRPGRoleTable? role = TrpgTalents.Roles.Find(x => x.Id == request.CharacterId);
            TRPGRoleTalentGroupTable? talent = role is null ? null : TrpgTalents.Talent(role, request.TalentId);

            if (role is null) response.Code = CharacterCfgNotExist;
            else if (!TargetFinished(session.player, role.UnlockTarget)) response.Code = CharacterNotOwner;
            else if (talent is null) response.Code = TalentCfgNotExist;
            else
            {
                TrpgRoleState state = TrpgTalents.RoleState(session.player, role.Id);
                if (state.Talents.Contains(talent.Id)) response.Code = TalentAlreadyActivate;
                else if (talent.PreId != 0 && !state.Talents.Contains(talent.PreId)) response.Code = TalentPreNotActivate;
                else if (TrpgExplore.MaxTalentPoints(session) - TrpgTalents.UsedPoints(session.player) < talent.CostPoint) response.Code = TalentPointNotEnough;
                else
                {
                    state.Talents.Add(talent.Id);
                    session.player.SaveChecked();
                    SendTalentEffects(session, role);
                }
            }

            session.SendResponse(response, packet.Id);
        }

        [RequestPacketHandler("TRPGResetTalentRequest")]
        public static void TRPGResetTalentRequestHandler(Session session, Packet.Request packet)
        {
            TRPGResetTalentRequest request = packet.Deserialize<TRPGResetTalentRequest>();
            TRPGResetTalentResponse response = new();
            TRPGRoleTable? role = TrpgTalents.Roles.Find(x => x.Id == request.CharacterId);
            TRPGRoleTalentTable cost = TableReaderV2.Parse<TRPGRoleTalentTable>().First();

            if (role is null) response.Code = CharacterCfgNotExist;
            else if (!TargetFinished(session.player, role.UnlockTarget)) response.Code = CharacterNotOwner;
            else if (TrpgTalents.RoleState(session.player, role.Id).Talents.Count == 0) response.Code = CharacterTalentsEmpty;
            else if (TrpgItems.Count(session, cost.ResetTalentItemId) < cost.ResetTalentItemCount) response.Code = UseItemCountNotEnough;
            else
            {
                TrpgTalents.RoleState(session.player, role.Id).Talents.Clear();
                session.player.SaveChecked();
                TrpgItems.TryConsume(session, cost.ResetTalentItemId, cost.ResetTalentItemCount);
                SendTalentEffects(session, role);
            }

            session.SendResponse(response, packet.Id);
        }

        /// <summary>Pushes what a talent change touches: the investigator, the carry limit and the shop modifiers.</summary>
        private static void SendTalentEffects(Session session, TRPGRoleTable role)
        {
            TrpgTalents.PushCharacters(session, [role]);
            session.SendPush(new NotifyTRPGItemCapacityChange { ItemCapacityAdd = TrpgTalents.ItemCapacityAdd(session) });
            TrpgPushShopExtra(session);
        }

        [RequestPacketHandler("TRPGUseItemRequest")]
        public static void TRPGUseItemRequestHandler(Session session, Packet.Request packet)
        {
            TRPGUseItemRequest request = packet.Deserialize<TRPGUseItemRequest>();
            TRPGUseItemResponse response = new();
            TRPGItemTable? item = TrpgItems.Config(request.ItemId);
            TRPGRoleTable? role = TrpgTalents.Roles.Find(x => x.Id == request.CharacterId);
            bool usable = item is { EffectType: ItemEffectClearBuff or ItemEffectAddBuff, ItemType: not ItemTypeSpecial };

            if (item is null) response.Code = ItemCfgNotExist;
            // AscNet rule: the client sends one use per investigator (XUiTRPGItemUsePanel passes 1); other counts are rejected.
            else if (request.ItemCount != 1) response.Code = UseItemCountError;
            else if (TrpgItems.Count(session, item.Id) < request.ItemCount) response.Code = UseItemCountNotEnough;
            else if (!usable) response.Code = ItemCanNotUse;
            else if (role is null || !TargetFinished(session.player, role.UnlockTarget)) response.Code = CharacterNotOwner;
            else
            {
                TrpgRoleState state = TrpgTalents.RoleState(session.player, role.Id);
                List<TRPGBuffTable> buffs = TableReaderV2.Parse<TRPGBuffTable>();
                List<TrpgBuffState> today = TrpgTalents.DayBuffs(state);
                List<TrpgBuffState> negative = today.Where(x => buffs.Find(b => b.Id == x.Id)?.EffectType == BuffEffectNegative).ToList();
                if (item.EffectType == ItemEffectAddBuff)
                {
                    int buffId = item.Params.First();
                    // AscNet rule: re-adding a buff that is already active today is refused so the item is not wasted.
                    if (!buffs.Any(x => x.Id == buffId) || today.Any(x => x.Id == buffId)) response.Code = ItemCanNotUse;
                    else state.Buffs.Add(new TrpgBuffState { Id = buffId, Period = TrpgTalents.Period });
                }
                else if (negative.Count == 0) response.Code = ItemCanNotUse;
                else
                {
                    // ClearBuff Params[0]: 1 removes one random negative buff, 0 removes all (item text).
                    IEnumerable<TrpgBuffState> removed = item.Params.FirstOrDefault() == 0 ? negative : [negative[Random.Shared.Next(negative.Count)]];
                    state.Buffs.RemoveAll(x => removed.Contains(x));
                }

                if (response.Code == 0)
                {
                    state.Buffs.RemoveAll(x => x.Period != TrpgTalents.Period);
                    session.player.SaveChecked();
                    TrpgItems.TryConsume(session, item.Id, request.ItemCount);
                    TrpgTalents.PushCharacters(session, [role]);
                }
            }

            session.SendResponse(response, packet.Id);
        }
    }
}
