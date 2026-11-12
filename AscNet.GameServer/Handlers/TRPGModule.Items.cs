using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.config;
using AscNet.Table.V2.share.trpg;
using MessagePack;

namespace AscNet.GameServer.Handlers
{
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    /// <summary>XTRPGManager.NotifyTRPGBaseInfo: exploration level, exp and endurance.</summary>
    [MessagePackObject(true)]
    public class NotifyTRPGBaseInfo
    {
        public NotifyTRPGData.NotifyTRPGDataBaseInfo BaseInfo { get; set; }
    }

    /// <summary>XTRPGManager.NotifyTRPGItemCapacityChange: talent-added carry limit.</summary>
    [MessagePackObject(true)]
    public class NotifyTRPGItemCapacityChange
    {
        public int ItemCapacityAdd { get; set; }
    }
#pragma warning restore CS8618

    /// <summary>TRPG items live in the ordinary inventory (client ItemManager type TRPGItem); only the carry limit is TRPG-specific.</summary>
    internal static class TrpgItems
    {
        internal static long Count(Session session, int itemId) => session.inventory.SpendableCount(itemId);

        internal static TRPGItemTable? Config(int itemId) => TableReaderV2.Parse<TRPGItemTable>().Find(x => x.Id == itemId);

        /// <summary>Carry limit (XTRPGConfigs.GetItemMaxCount + talent add) for TRPG items; unlimited for every other item.</summary>
        internal static int MaxCount(Session session, int itemId) =>
            Config(itemId) is { } row ? row.Capacity + TrpgTalents.ItemCapacityAdd(session) : int.MaxValue;

        /// <summary>Spends the items; false (nothing changed) when count is not positive or the bag is short.</summary>
        internal static bool TryConsume(Session session, int itemId, int count)
        {
            if (count <= 0 || session.inventory.SpendableCount(itemId) < count) return false;
            List<Item> changed = session.inventory.Spend(itemId, count);
            session.inventory.SaveChecked();
            session.SendPush(new NotifyItemDataList { ItemDataList = changed });
            return true;
        }

        /// <summary>Adds the items, stopping at the carry limit.</summary>
        internal static void Grant(Session session, int itemId, int count)
        {
            long room = MaxCount(session, itemId) - Count(session, itemId);
            int add = (int)Math.Min(count, Math.Max(0, room));
            if (add <= 0) return;
            session.inventory.Do(itemId, add);
            session.inventory.SaveChecked();
            session.SendPush(new NotifyItemDataList { ItemDataList = session.inventory.SpendableStacks(itemId).ToList() });
            if (itemId == TrpgExplore.ExpItemId) session.SendPush(TrpgExplore.BaseInfoPush(session));
            TRPGModule.RefreshDerivedTargets(session);
        }
    }

    /// <summary>Exploration level (from the exp item), endurance and talent points.</summary>
    internal static class TrpgExplore
    {
        /// <summary>The inventory count of this item is the cumulative exploration exp; level and in-level exp derive from TRPGLevel.</summary>
        internal const int ExpItemId = 61002;

        private static List<TRPGLevelTable> Levels => TableReaderV2.Parse<TRPGLevelTable>().OrderBy(x => x.Id).ToList();

        internal static int MaxEndurance =>
            int.Parse(TableReaderV2.Parse<ConfigTable>().Single(row => row.Key == "TrpgMaxEndurance").Value);

        internal static (int Level, int Exp) LevelOf(long totalExp)
        {
            List<TRPGLevelTable> levels = Levels;
            int level = levels[0].Id;
            foreach (TRPGLevelTable row in levels)
            {
                level = row.Id;
                // UpExp 0 marks the last level.
                if (row.UpExp <= 0 || totalExp < row.UpExp) return (level, (int)Math.Min(totalExp, int.MaxValue));
                totalExp -= row.UpExp;
            }
            return (level, (int)Math.Min(totalExp, int.MaxValue));
        }

        /// <summary>Talent points granted at the current level (XTRPGManager.GetMaxTalentPoint).</summary>
        internal static int MaxTalentPoints(Session session) =>
            Levels.First(x => x.Id == LevelOf(TrpgItems.Count(session, ExpItemId)).Level).TalentPoint;

        /// <summary>Refills endurance once per daily reset period (item 61003: "restored at the daily reset"). Fresh accounts start full.</summary>
        internal static void RefreshEndurance(Session session)
        {
            TrpgState state = session.player.Trpg;
            long period = TaskModule.CurrentDailyResetPeriod(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            if (state.EndurancePeriod == period) return;
            state.Endurance = MaxEndurance;
            state.EndurancePeriod = period;
            session.player.SaveChecked();
        }

        internal static NotifyTRPGData.NotifyTRPGDataBaseInfo BaseInfo(Session session)
        {
            RefreshEndurance(session);
            (int level, int exp) = LevelOf(TrpgItems.Count(session, ExpItemId));
            return new() { Level = level, Exp = exp, Endurance = session.player.Trpg.Endurance };
        }

        internal static NotifyTRPGBaseInfo BaseInfoPush(Session session) => new() { BaseInfo = BaseInfo(session) };

        /// <summary>Spends endurance; false (nothing changed) when there is not enough.</summary>
        internal static bool TryConsumeEndurance(Session session, int amount)
        {
            RefreshEndurance(session);
            TrpgState state = session.player.Trpg;
            if (amount < 0 || state.Endurance < amount) return false;
            if (amount == 0) return true;
            state.Endurance -= amount;
            session.player.SaveChecked();
            session.SendPush(BaseInfoPush(session));
            return true;
        }
    }
}
