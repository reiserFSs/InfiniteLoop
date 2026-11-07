using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Game;
using AscNet.Table.V2.share.exhibition;
using AscNet.Table.V2.share.fashion;
using AscNet.Table.V2.share.mailcollectionbox;
using AscNet.Table.V2.share.reward;
using MessagePack;

namespace AscNet.GameServer.Handlers
{
    #region MsgPackScheme
#pragma warning disable CS8618
    [MessagePackObject(true)]
    public class GatherRewardListResponse
    {
        public int Code;
        public List<int> GatherRewards { get; set; } = new();
    }

    [MessagePackObject(true)]
    public class CharacterSetLiberateAureoleIdRequest
    {
        public int CharacterId;
        public int AureoleId;
    }

    [MessagePackObject(true)]
    public class CharacterSetLiberateAureoleIdResponse
    {
        public int Code;
    }

    [MessagePackObject(true)]
    public class MailCollectionBoxReceiveFavoriteMailIdsRequest
    {
        // The client's XMessagePack encodes an empty Lua table as nil.
        public List<int>? FavoriteMailIds { get; set; }
    }

    [MessagePackObject(true)]
    public class MailCollectionBoxReceiveFavoriteMailIdsResponse
    {
        public int Code;
        // Client reads #response.SuccessedIds even on failure: always non-null.
        public List<int> SuccessedIds { get; set; } = new();
        public List<RewardGoods> Rewards { get; set; } = new();
    }
#pragma warning restore CS8618
    #endregion

    internal class CollectionMiscModule
    {
        // XEnumConst.CHARACTER.GrowUpLevel.Super: XUiPanelExhibitionSuperInfo disables the aureole button below it.
        internal const int SuperLiberateLevel = 5;

        // CodeText.tsv
        private const int AureoleConfigNotFound = 20010006;      // FashionAureoleConfigNotFound
        private const int AureoleInvalidCharacter = 20010007;    // FashionAureoleInvalidCharacterId
        private const int AureoleLiberateLvNotEnough = 20010008; // FashionAureoleLiberateLvNotEnough
        private const int AureoleIdNotFound = 20010009;          // FashionAureoleIdNotFound
        private const int ExhibitionLimitNotFound = 20009061;    // CharacterExhibitionLimitCfgNotFound
        private const int MailRepeat = 20027003;                 // MailManagerGetRewardRepeat
        private const int MailNotEffect = 20027004;              // MailManagerMailNotEffect
        private const int MailIdInvalid = 20027007;              // MailManagerMailIdInvalid
        private const int MailRewardMissing = 20027010;          // MailManagerGetMailRewardListNotFoundReward

        /// <summary>
        /// AscNet policy (Maintainer-authorized missing-calendar availability, AGENTS.md): an activity whose
        /// TimeId has no authoritative calendar window is permanently open; a known window is honoured.
        /// </summary>
        internal static bool IsFavoriteMailActivityOpen(MailCollectionBoxActivityTable activity, DateTimeOffset now) =>
            activity.TimeId is not > 0
            || !ActivityScheduleService.TryGet(activity.TimeId.Value, out ActivityScheduleEntry window)
            || window.IsOpen(now);

        internal static NotifyLoginMailCollectionBoxData BuildLoginData(Player player, DateTimeOffset now) => new()
        {
            OpenActivityIds = TableReaderV2.Parse<MailCollectionBoxActivityTable>(player.ClientRegion)
                .Where(activity => IsFavoriteMailActivityOpen(activity, now)).Select(activity => activity.Id).ToList(),
            MailCollectionBoxDataDb = { ReceivedFavoriteMailIds = player.ReceivedFavoriteMailIds.ToList() }
        };

        [RequestPacketHandler("GatherRewardListRequest")]
        public static void GatherRewardListRequestHandler(Session session, Packet.Request packet)
        {
            session.SendResponse(new GatherRewardListResponse { GatherRewards = session.player.GatherRewards.ToList() }, packet.Id);
        }

        [RequestPacketHandler("CharacterSetLiberateAureoleIdRequest")]
        public static void CharacterSetLiberateAureoleIdRequestHandler(Session session, Packet.Request packet)
        {
            CharacterSetLiberateAureoleIdRequest request = packet.Deserialize<CharacterSetLiberateAureoleIdRequest>();
            CharacterData? character = session.character.Characters.Find(c => c.Id == request.CharacterId);
            ExhibitionLimitTable? limit = TableReaderV2.Parse<ExhibitionLimitTable>().Find(row => row.CharacterId == request.CharacterId);
            int code;
            if (character is null)
                code = AureoleInvalidCharacter;
            else if (limit is null)
                code = ExhibitionLimitNotFound;
            else if (!TableReaderV2.Parse<FashionAureoleTable>().Any(row => row.Id == request.AureoleId))
                code = AureoleConfigNotFound;
            else if (!limit.AureoleIds.Contains(request.AureoleId))
                code = AureoleIdNotFound;
            else if (Math.Max(character.LiberateLv, Character.GetLiberateLevel(character.Id, session.player.GatherRewards)) < SuperLiberateLevel)
                code = AureoleLiberateLvNotEnough;
            else
                code = 0;

            if (code == 0)
            {
                if (character!.LiberateAureoleId != request.AureoleId)
                {
                    character.LiberateAureoleId = request.AureoleId;
                    session.character.SaveChecked();
                }
                session.SendPush(new NotifyCharacterDataList { CharacterDataList = { character } });
            }
            session.SendResponse(new CharacterSetLiberateAureoleIdResponse { Code = code }, packet.Id);
        }

        [RequestPacketHandler("MailCollectionBoxReceiveFavoriteMailIdsRequest")]
        public static void MailCollectionBoxReceiveFavoriteMailIdsRequestHandler(Session session, Packet.Request packet)
        {
            List<int> requested = (packet.Deserialize<MailCollectionBoxReceiveFavoriteMailIdsRequest>().FavoriteMailIds ?? []).Distinct().ToList();
            DateTimeOffset now = DateTimeOffset.UtcNow;
            HashSet<int> receivable = TableReaderV2.Parse<MailCollectionBoxActivityTable>(session.player.ClientRegion)
                .Where(activity => IsFavoriteMailActivityOpen(activity, now))
                .SelectMany(activity => activity.FavoriteMailIds).ToHashSet();
            Dictionary<int, MailCollectionBoxFavoriteMailTable> mails = TableReaderV2.Parse<MailCollectionBoxFavoriteMailTable>(session.player.ClientRegion)
                .ToDictionary(row => row.Id);

            int firstReject = MailIdInvalid;
            bool rejected = false;
            List<int> accepted = [];
            List<RewardGrant> grants = [];
            foreach (int id in requested)
            {
                int reject = !mails.TryGetValue(id, out MailCollectionBoxFavoriteMailTable? mail) ? MailIdInvalid
                    : session.player.ReceivedFavoriteMailIds.Contains(id) ? MailRepeat
                    : !receivable.Contains(id) ? MailNotEffect
                    : 0;
                List<RewardGoodsTable> goods = reject != 0 ? [] : mail!.RewardIds.Where(rewardId => rewardId > 0)
                    .SelectMany(RewardHandler.GetRewardGoods).ToList();
                if (reject == 0 && mail!.RewardIds.Any(rewardId => rewardId > 0) && goods.Count == 0)
                    reject = MailRewardMissing;
                if (reject != 0)
                {
                    if (!rejected)
                        firstReject = reject;
                    rejected = true;
                    continue;
                }
                accepted.Add(id);
                if (goods.Count > 0)
                    grants.Add(new RewardGrant($"favorite-mail:{id}", goods));
            }
            // Partial success reports Code 0 with the accepted ids; failure only when nothing was accepted.
            int code = accepted.Count > 0 ? 0 : firstReject;

            MailCollectionBoxReceiveFavoriteMailIdsResponse response = new() { Code = code };
            RewardApplicationResult? rewards = null;
            if (accepted.Count > 0)
            {
                // Claim keys make a retry after a failed receipt save replay-safe.
                if (grants.Count > 0)
                    rewards = RewardHandler.ApplyRewardsOnceAndPersist(grants, session);
                session.player.ReceivedFavoriteMailIds.AddRange(accepted);
                session.player.SaveChecked();
                response.SuccessedIds = accepted;
                response.Rewards = rewards?.RewardGoods ?? [];
            }
            rewards?.SendPushes(session);
            session.SendResponse(response, packet.Id);
        }
    }
}
