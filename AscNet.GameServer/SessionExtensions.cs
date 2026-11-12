using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.player;

namespace AscNet.GameServer
{
    public static class SessionExtensions
    {
        private static readonly Lazy<PlayerTable[]> OrderedPlayerLevels = new(
            static () => TableReaderV2.Parse<PlayerTable>()
                .OrderBy(static playerLevel => playerLevel.Level)
                .ToArray());

        /// <summary>
        /// Please invoke after messing with TeamExp(commandant exp) count!
        /// </summary>
        /// <param name="session"></param>
        public static void ExpSanityCheck(this Session session)
        {
            PlayerTable[] playerLevels = OrderedPlayerLevels.Value;
            if (playerLevels.Length == 0)
                return;

            if (ClampPlayerLevelToConfiguredMaximum(session, playerLevels))
            {
                session.SendPush(new NotifyPlayerLevel
                {
                    Level = (int)session.player.PlayerData.Level
                });
            }

            Item? expItem = session.inventory.Items.FirstOrDefault(x => x.Id == Inventory.TeamExp);
            if (expItem is null)
                return;

            foreach ((PlayerTable playerLevel, int nextLevel) in EarnedCommandantLevels(session.player.PlayerData.Level, expItem.Count))
            {
                expItem.Count -= playerLevel.MaxExp;
                session.player.PlayerData.Level = nextLevel;
                session.SendPush(new NotifyPlayerLevel { Level = (int)session.player.PlayerData.Level });
                session.SendPush(new NotifyItemDataList
                {
                    ItemDataList = [expItem, session.inventory.Do(Inventory.ActionPoint, playerLevel.FreeActionPoint)]
                });
            }
        }

        internal static IEnumerable<(PlayerTable Level, int NextLevel)> EarnedCommandantLevels(long level, long exp)
        {
            PlayerTable[] playerLevels = OrderedPlayerLevels.Value;
            int playerLevelIndex = 0;
            while (playerLevelIndex < playerLevels.Length
                && playerLevels[playerLevelIndex].Level != level)
            {
                playerLevelIndex++;
            }

            while (playerLevelIndex + 1 < playerLevels.Length)
            {
                PlayerTable playerLevel = playerLevels[playerLevelIndex++];
                if (exp < playerLevel.MaxExp)
                    yield break;
                exp -= playerLevel.MaxExp;
                yield return (playerLevel, playerLevels[playerLevelIndex].Level);
            }
        }

        internal static bool CommitPendingCommandantLevel(this Session session)
        {
            int target = session.inventory.PendingCommandantLevel;
            if (target == 0)
                return false;
            long previous = session.player.PlayerData.Level;
            if (target > previous)
            {
                session.player.PlayerData.Level = target;
                try { session.player.SaveChecked(); }
                catch
                {
                    session.player.PlayerData.Level = previous;
                    throw;
                }
            }
            session.inventory.PendingCommandantLevel = 0;
            try { session.inventory.SaveChecked(); }
            catch
            {
                session.inventory.PendingCommandantLevel = target;
                throw;
            }
            return true;
        }

        internal static void RecoverPendingCommandantLevel(this Session session)
        {
            if (!session.CommitPendingCommandantLevel())
                return;
            session.SendPush(new NotifyPlayerLevel { Level = (int)session.player.PlayerData.Level });
            session.SendPush(new NotifyItemDataList
            {
                ItemDataList = session.inventory.Items.Where(item => item.Id is Inventory.TeamExp or Inventory.ActionPoint).ToList()
            });
        }

        public static bool ClampPlayerLevelToConfiguredMaximum(this Session session)
        {
            return ClampPlayerLevelToConfiguredMaximum(session, OrderedPlayerLevels.Value);
        }

        private static bool ClampPlayerLevelToConfiguredMaximum(Session session, PlayerTable[] playerLevels)
        {
            if (playerLevels.Length == 0)
                return false;

            int maximumLevel = playerLevels[^1].Level;

            if (session.player.PlayerData.Level <= maximumLevel)
                return false;

            session.player.PlayerData.Level = maximumLevel;
            return true;
        }
    }
}
