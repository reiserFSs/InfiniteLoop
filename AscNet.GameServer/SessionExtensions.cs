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

        internal static int? MaximumConfiguredPlayerLevel => OrderedPlayerLevels.Value.LastOrDefault()?.Level;

        internal static bool IsConfiguredPlayerLevel(int level) =>
            level > 0 && OrderedPlayerLevels.Value.Any(row => row.Level == level);

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

            int playerLevelIndex = 0;
            while (playerLevelIndex < playerLevels.Length
                && playerLevels[playerLevelIndex].Level != session.player.PlayerData.Level)
            {
                playerLevelIndex++;
            }

            if (playerLevelIndex == playerLevels.Length)
                return;

            while (playerLevelIndex + 1 < playerLevels.Length)
            {
                PlayerTable playerLevel = playerLevels[playerLevelIndex];
                if (expItem.Count < playerLevel.MaxExp)
                    return;

                PlayerTable nextPlayerLevel = playerLevels[++playerLevelIndex];
                expItem.Count -= playerLevel.MaxExp;
                session.player.PlayerData.Level = nextPlayerLevel.Level;

                NotifyPlayerLevel notifyPlayerLevel = new()
                {
                    Level = (int)session.player.PlayerData.Level
                };
                NotifyItemDataList notifyItemDataList = new();
                notifyItemDataList.ItemDataList.Add(expItem);
                notifyItemDataList.ItemDataList.Add(session.inventory.Do(Inventory.ActionPoint, playerLevel.FreeActionPoint));

                session.SendPush(notifyPlayerLevel);
                session.SendPush(notifyItemDataList);
            }
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
