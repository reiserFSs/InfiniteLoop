using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using MessagePack;

namespace AscNet.GameServer.Handlers
{
    #region MsgPackScheme
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    [MessagePackObject(true)]
    public class LifeTreeFinishProcessRequest
    {
        public int Process { get; set; }
    }

    [MessagePackObject(true)]
    public class LifeTreeFinishProcessResponse
    {
        public int Code { get; set; }
    }
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    #endregion

    internal class LifeTreeModule
    {
        private static readonly int[] FinishedProcessOneChapters = [61, 58, 57];
        private static readonly int[] FinishedProcessOneCharacterIds = [1031005, 1021007];

        [RequestPacketHandler("LifeTreeFinishProcessRequest")]
        public static void LifeTreeFinishProcessRequestHandler(Session session, Packet.Request packet)
        {
            LifeTreeFinishProcessRequest request = packet.Deserialize<LifeTreeFinishProcessRequest>();
            bool handled = FinishProcess(session.player, request.Process);
            session.log.Debug($"LifeTreeFinishProcessRequest Process={request.Process}");
            if (handled)
                session.SendPush(BuildNotifyLifeTreeData(session.player));

            session.SendResponse(new LifeTreeFinishProcessResponse(), packet.Id);
        }

        internal static NotifyLifeTreeData BuildNotifyLifeTreeData(Player player)
        {
            player.LifeTreeData ??= new();
            return player.LifeTreeData;
        }

        private static bool FinishProcess(Player player, int process)
        {
            if (process is not (1 or 2))
                return false;

            player.LifeTreeData ??= new();
            NotifyLifeTreeData data = player.LifeTreeData;
            bool changed = false;

            if (!data.IsFinishGuide)
            {
                data.IsFinishGuide = true;
                changed = true;
            }

            if (!data.IsFinishLifeTreePv)
            {
                data.IsFinishLifeTreePv = true;
                changed = true;
            }

            foreach (int chapterId in FinishedProcessOneChapters)
            {
                if (!data.FinishedChapters.Contains(chapterId))
                {
                    data.FinishedChapters.Add(chapterId);
                    changed = true;
                }
            }

            foreach (int characterId in FinishedProcessOneCharacterIds)
            {
                if (!data.UnlockCharacterData.TryGetValue(characterId, out LifeTreeUnlockCharacterData? characterData)
                    || characterData.UnlockStatus != 1)
                {
                    data.UnlockCharacterData[characterId] = new LifeTreeUnlockCharacterData
                    {
                        Id = characterId,
                        UnlockStatus = 1
                    };
                    changed = true;
                }
            }

            if (changed)
            {
                data.FinishedChapters = data.FinishedChapters
                    .Distinct()
                    .OrderByDescending(chapterId => chapterId)
                    .ToList();
                player.Save();
            }

            return true;
        }
    }
}
