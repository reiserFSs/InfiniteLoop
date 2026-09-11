using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.theatre;

namespace AscNet.GameServer.Handlers;

internal static partial class TheatreModule
{
    internal static bool HasPendingSkillChoice(Mutation m) =>
        m.Data.CurChapterDb?.SkillToSelect.Count > 0 || m.State.PendingSkillPowers.Count > 0;

    internal static int DrawSkillPower(Mutation m) => DrawSkillChoice(m, 0, powerOnly: true).PowerId;

    // LOCAL allocation: one weighted faction, then up to three distinct skills from it.
    // Rule Id follows completed-node ordinal, capped at the last authored rule.
    // Faction weight = max(1, basic + Factor5*(expected-owned count) + token factor).
    // Skill weight = max(1, basic + authored weight + Factor6*(expected grid level-current)).
    // Favor type3 sets initial quality; type4 adds its highest bonus to the normal +1 upgrade.
    private static (int PowerId, List<int> Skills) DrawSkillChoice(Mutation m, int powerId, bool powerOnly = false)
    {
        var all = Rows<TheatreSkillTable>();
        var owned = all.Where(skill => m.Data.Skills.Contains(skill.Id)).ToList();
        var favor = Rows<TheatrePowerFavorTable>().Where(row => m.Data.EffectPowerFavorIds.Contains(row.Id)).ToList();
        var candidates = new List<TheatreSkillTable>();
        foreach (var skill in all)
        {
            if (!m.Data.UnlockPowerIds.Contains(skill.PowerId) || (powerId != 0 && skill.PowerId != powerId)) continue;
            int FavorValue(int type) => favor.Where(row => row.PowerId == skill.PowerId)
                .SelectMany(row => row.RewardType.Select((value, index) => value == type ? row.RewardParam[index] : 0))
                .DefaultIfEmpty(0).Max();
            if (skill.Type == 2)
            {
                bool unlocked = skill.NeedPowerFavor.GetValueOrDefault() == 0 || favor.Any(row => row.PowerId == skill.PowerId
                    && row.RewardType.Select((type, index) => type == 2 && row.RewardParam[index] == skill.Id).Any(value => value));
                if (unlocked && !m.Data.Skills.Contains(skill.Id)) candidates.Add(skill);
            }
            else if (skill.Type == 1)
            {
                var current = owned.SingleOrDefault(row => row.Type == 1 && row.Pos == skill.Pos);
                bool upgrade = current != null && current.PowerId == skill.PowerId;
                int maxQuality = all.Where(row => row.Type == 1 && row.PowerId == skill.PowerId && row.Pos == skill.Pos)
                    .Max(row => row.Quality.GetValueOrDefault());
                int quality = upgrade ? Math.Min(maxQuality, checked(current!.Quality.GetValueOrDefault() + 1 + FavorValue(4)))
                    : Math.Min(maxQuality, Math.Max(1, FavorValue(3)));
                if (skill.Quality == quality && (!upgrade || current!.Id != skill.Id)) candidates.Add(skill);
            }
            else throw new InvalidDataException($"Unsupported Theatre skill type {skill.Type}.");
        }
        Require(candidates.Count > 0, 20155023);
        var rules = Rows<TheatreSkillRuleTable>();
        var rule = rules.OrderBy(row => row.Id).Last(row => row.Id <= Math.Min(checked(m.Data.PassNodeCount + 1), rules.Max(value => value.Id)));
        double powerFactor = Rows<TheatreFactorTable>().Single(row => row.Id == 5).Factor;
        double gridFactor = Rows<TheatreFactorTable>().Single(row => row.Id == 6).Factor;
        var keepsake = m.Data.Keepsakes.SingleOrDefault(row => row.KeepsakeId == m.Data.KeepsakeId);
        var token = keepsake == null ? null : Rows<TheatreItemTable>().Single(row => row.Type == 1
            && row.KeepsakeId == keepsake.KeepsakeId && row.Lv == keepsake.Lv);
        if (powerId == 0)
        {
            var powers = candidates.Select(row => row.PowerId).Distinct().Select(id => (Id: id, Weight: Math.Max(1d,
                rule.PowerBasicWeight + powerFactor * (rule.PowerExpect[id - 1] - owned.Count(row => row.PowerId == id))
                + (token?.PowerId == id ? token.PowerFactor.GetValueOrDefault() : 0)))).ToList();
            powerId = DrawSkillWeighted(powers);
        }
        if (powerOnly) return (powerId, []);
        var pool = candidates.Where(row => row.PowerId == powerId).Select(row =>
        {
            int pos = row.Pos.GetValueOrDefault();
            int level = owned.Where(value => value.Type == 1 && value.Pos == pos).Select(value => value.Lv.GetValueOrDefault()).SingleOrDefault();
            double adjustment = pos > 0 ? gridFactor * (rule.LvExpect[pos - 1] - level) : 0;
            return (Id: row.Id, Weight: Math.Max(1d, rule.SkillBasicWeight + row.SkillWeight + adjustment));
        }).ToList();
        List<int> result = [];
        while (result.Count < 3 && pool.Count > 0)
        {
            int id = DrawSkillWeighted(pool);
            result.Add(id);
            pool.RemoveAll(row => row.Id == id);
        }
        return (powerId, result);
    }

    private static int DrawSkillWeighted(List<(int Id, double Weight)> pool)
    {
        double target = Random.Shared.NextDouble() * pool.Sum(row => row.Weight);
        foreach (var row in pool)
        {
            target -= row.Weight;
            if (target < 0) return row.Id;
        }
        return pool[^1].Id;
    }

    internal static void QueueSkillChoice(Mutation m, int powerId = 0)
    {
        Require(m.Data.CurChapterDb != null, 20155005);
        if (HasPendingSkillChoice(m)) m.State.PendingSkillPowers.Add(powerId);
        else PublishSkillChoice(m, DrawSkillChoice(m, powerId).Skills);
    }

    private static void PublishSkillChoice(Mutation m, List<int> skills)
    {
        m.Data.CurChapterDb!.SkillToSelect = skills.ToList();
        m.Push(new NotifyTheatreNodeReward { RewardType = 1, Skills = skills.ToList() });
    }

    internal static void ActivateShopSkillChoice(Mutation m, TheatreShopItem item)
    {
        Require(!HasPendingSkillChoice(m) && m.State.ShopSkillOpened && item.Skills.Count > 0, 20155023);
        m.State.PendingSkillShopType = 1;
        PublishSkillChoice(m, item.Skills);
    }

    private static void FinishSkillChoice(Mutation m)
    {
        m.Data.CurChapterDb!.SkillToSelect.Clear();
        m.State.PendingSkillShopType = 0;
        if (m.State.PendingSkillPowers.Count > 0)
        {
            int power = m.State.PendingSkillPowers[0];
            m.State.PendingSkillPowers.RemoveAt(0);
            PublishSkillChoice(m, DrawSkillChoice(m, power).Skills);
        }
        else if (m.State.NodeCompletionPending) GenerateNextNode(m);
    }

    [RequestPacketHandler("TheatreNodeShopOpenSkillRequest")]
    public static void NodeShopOpenSkill(Session session, Packet.Request packet) =>
        Handle<TheatreNodeShopOpenSkillRequest, TheatreNodeShopOpenSkillResponse>(session, packet, (m, request, response) =>
        {
            var slot = CurrentSlot(m);
            Require(slot.SlotType == 2, 20155025);
            var item = slot.ShopItems.SingleOrDefault(value => value.ItemType == 1);
            Require(item != null, 20155026);
            Require(!HasPendingSkillChoice(m), 20155023);
            if (!m.State.ShopSkillOpened)
            {
                Require(item!.IsBuy == 0, 20155027);
                var choice = DrawSkillChoice(m, item.PowerId);
                item.PowerId = choice.PowerId;
                item.Skills = choice.Skills;
                m.State.ShopSkillOpened = true;
            }
            response.Skills = item.Skills.ToList();
            response.PowerId = item.PowerId;
        });

    [RequestPacketHandler("TheatreSelectSkillRequest")]
    public static void SelectSkill(Session session, Packet.Request packet) =>
        Handle<TheatreSelectSkillRequest, TheatreSelectSkillResponse>(session, packet, (m, request, response) =>
        {
            Require(m.Data.CurChapterDb != null, 20155005);
            Require(m.Data.CurChapterDb!.SkillToSelect.Contains(request.SkillId), 20155023);
            var all = Rows<TheatreSkillTable>();
            var skill = all.Single(row => row.Id == request.SkillId);
            if (skill.Type == 1)
                m.Data.Skills.RemoveAll(id => all.Any(row => row.Id == id && row.Type == 1 && row.Pos == skill.Pos));
            else Require(skill.Type == 2 && !m.Data.Skills.Contains(skill.Id), 20155023);
            m.Data.Skills.Add(skill.Id);
            if (!m.Data.SkillIllustratedBook.Contains(skill.Id)) m.Data.SkillIllustratedBook.Add(skill.Id);
            UpdateOwnCharacterPermission(m);
            FinishSkillChoice(m);
        });

    [RequestPacketHandler("TheatreSkipSelectSkillRequest")]
    public static void SkipSelectSkill(Session session, Packet.Request packet) =>
        Handle<TheatreSkipSelectSkillRequest, TheatreSkipSelectSkillResponse>(session, packet, (m, request, response) =>
        {
            Require(m.Data.CurChapterDb?.SkillToSelect.Count > 0, 20155023);
            FinishSkillChoice(m);
        });

    internal static void QueueLevelUp(Mutation m, int count = 1)
    {
        Require(count > 0 && m.Data.CurChapterDb != null, 20155005);
        var levels = Rows<TheatreLvTable>().OrderBy(row => row.Lv).ToList();
        int current = levels.FindIndex(row => row.Lv == m.Data.CurRoleLv);
        Require(current >= 0, 20155032);
        int level = levels[(int)Math.Min(levels.Count - 1L, (long)current + count)].Lv;
        RemapRoleLevel(m, level);
        m.Push(new NotifyTheatreNodeReward { RewardType = 2, Lv = level });
    }

    internal static void ApplyNodeReward(Mutation m, int rewardType, int count = 1, int powerId = 0)
    {
        Require(count > 0, 1);
        switch (rewardType)
        {
            case 0: return;
            case 1:
                for (int i = 0; i < count; i++) QueueSkillChoice(m, powerId);
                return;
            case 2: QueueLevelUp(m, count); return;
            case 3:
                int decoration = GetModifiers(m).InspirationGain(count);
                m.Data.DecorationCoin = checked(m.Data.DecorationCoin + decoration);
                m.Push(new NotifyTheatreCoinChange { FavorCoin = m.Data.FavorCoin, DecorationCoin = m.Data.DecorationCoin });
                m.Push(new NotifyTheatreNodeReward { RewardType = 3, DecorationPoint = decoration });
                return;
            case 4:
                int favor = GetModifiers(m).CadenzaGain(count);
                m.Data.FavorCoin = checked(m.Data.FavorCoin + favor);
                m.Push(new NotifyTheatreCoinChange { FavorCoin = m.Data.FavorCoin, DecorationCoin = m.Data.DecorationCoin });
                m.Push(new NotifyTheatreNodeReward { RewardType = 4, FavorPoint = favor });
                return;
            case 99:
                // RewardId is carried by the caller in count, not guessed from the reward enum.
                m.Grant(count);
                return;
            default: throw new InvalidDataException($"Unsupported Theatre node reward type {rewardType}.");
        }
    }

    internal static void RecordKeepsakeNode(Mutation m)
    {
        var keepsake = m.Data.Keepsakes.SingleOrDefault(row => row.KeepsakeId == m.Data.KeepsakeId);
        if (keepsake == null) return;
        var rows = Rows<TheatreItemTable>().Where(row => row.Type == 1 && row.KeepsakeId == keepsake.KeepsakeId).ToList();
        var current = rows.Single(row => row.Lv == keepsake.Lv);
        int threshold = current.FightCount.GetValueOrDefault();
        if (threshold <= 0) return;
        // The authored counter is named FightCount, but its explanation explicitly counts nodes.
        keepsake.FightCount = checked(keepsake.FightCount + 1);
        if (keepsake.FightCount >= threshold)
        {
            keepsake.FightCount -= threshold;
            keepsake.Lv = rows.Where(row => row.Lv > keepsake.Lv).Min(row => row.Lv)!.Value;
        }
        m.Push(new NotifyTheatreKeepsakeUpgrade { KeepsakeId = keepsake.KeepsakeId, Lv = keepsake.Lv, FightCount = keepsake.FightCount });
    }
}
