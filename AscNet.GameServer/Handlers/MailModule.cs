using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.reward;
using MessagePack;
using AscNet.Table.V2.share.item;
using AscNet.Table.V2.client.mail;
using AscNet.Table.V2.share.mail;
using AscNet.Table.V2.share.character;
using AscNet.Table.V2.share.equip;
using AscNet.Table.V2.share.fashion;
using Newtonsoft.Json.Linq;

namespace AscNet.GameServer.Handlers
{
    internal class MailModule
    {
        private const int Fail = 20027001;
        private const int Empty = 20027002;
        private const int Repeat = 20027003;
        private const int NotEffect = 20027004;
        private const int Invalid = 20027005;
        private const int ExpiredOrEmpty = 20027006;
        private const int InvalidId = 20027007;
        private const int NotExist = 20027009;
        private const int MissingReward = 20027010;
        private const int Deleted = 20027012;
        private const int Read = 20027013;
        private const int Expired = 20027015;
        private const int Withdrawn = 20027016;

        internal static NotifyMails BuildNotifyMails(Player player, long now)
        {
            ReconcileExpiry(player, now);
            return new NotifyMails
            {
                NewMailList = player.Mails.Where(mail => IsVisible(mail, now)).Select(ToNotify).ToList(),
                ExpireIdList = player.MailExpireIds.Count == 0 ? null : player.MailExpireIds.ToList()
            };
        }

        internal static bool EnsureSystemMails(Player player, long now)
        {
            bool changed = false;
            JObject config = JsonSnapshot.LoadObject("Configs/system_mails.json");
            IEnumerable<JObject> definitions = config["Mails"] is JArray mails ? mails.OfType<JObject>() : [];
            foreach (JObject definition in definitions)
            {
                string? id = definition.Value<string>("Id");
                if (string.IsNullOrWhiteSpace(id)
                    || player.Mails.Any(mail => mail.Id == id)
                    || player.MailExpireIds.Contains(id))
                    continue;
                List<PlayerMailRewardGoods>? rewards = definition["MailRewardId"] is null
                    ? null
                    : ResolveMailRewards(definition.Value<int>("MailRewardId"));
                if (definition["MailRewardId"] is not null && rewards is null)
                    continue;
                player.Mails.Add(new PlayerMail
                {
                    Id = id,
                    GroupId = definition.Value<int?>("GroupId") ?? 0,
                    BatchId = definition.Value<string>("BatchId"),
                    Type = definition.Value<int?>("Type") ?? 0,
                    Status = 0,
                    SendName = definition.Value<string>("SendName") ?? string.Empty,
                    Title = definition.Value<string>("Title") ?? string.Empty,
                    Content = definition.Value<string>("Content") ?? string.Empty,
                    CreateTime = now,
                    SendTime = definition.Value<long?>("SendTime") ?? now,
                    ExpireTime = definition.Value<long?>("ExpireTime") ?? 0,
                    RewardGoodsList = rewards,
                    IsForbidDelete = definition.Value<bool?>("IsForbidDelete") ?? false,
                    IsSurvey = definition.Value<bool?>("IsSurvey") ?? false,
                    ReserveTime = definition.Value<long?>("ReserveTime") ?? 0
                });
                changed = true;
            }
            return changed;
        }

        [RequestPacketHandler("MailReadRequest")]
        public static void MailReadRequestHandler(Session session, Packet.Request packet)
        {
            MailReadRequest request = packet.Deserialize<MailReadRequest>();
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (ReconcileExpiry(session.player, now))
            {
                try { session.player.SaveChecked(); }
                catch { session.SendResponse(new MailReadResponse { Code = Fail }, packet.Id); return; }
            }
            PlayerMail? mail = Find(session.player, request.Id);
            int code = ValidateRead(mail, now);
            if (code == 0 && mail!.Status == 0)
            {
                mail.Status = 1;
                try { session.player.SaveChecked(); }
                catch { mail.Status = 0; code = Fail; }
            }
            else if (code == 0 && mail!.Status == 1)
                code = Read;
            session.SendResponse(new MailReadResponse { Code = code }, packet.Id);
        }

        [RequestPacketHandler("MailDeleteRequest")]
        public static void MailDeleteRequestHandler(Session session, Packet.Request packet)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (ReconcileExpiry(session.player, now))
            {
                try { session.player.SaveChecked(); }
                catch { session.SendResponse(new MailDeleteResponse(), packet.Id); return; }
            }
            List<(int Index, PlayerMail Mail)> removed = session.player.Mails
                .Select((mail, index) => (index, mail))
                .Where(entry => !entry.mail.IsForbidDelete
                    && ((entry.mail.Status == 1 && entry.mail.RewardGoodsList is not { Count: > 0 })
                        || entry.mail.Status == 3))
                .Select(entry => (entry.index, entry.mail))
                .ToList();
            if (removed.Count > 0)
            {
                session.player.Mails.RemoveAll(mail => removed.Any(entry => entry.Mail == mail));
                try { session.player.SaveChecked(); }
                catch
                {
                    foreach ((int index, PlayerMail mail) in removed.OrderBy(entry => entry.Index))
                        session.player.Mails.Insert(index, mail);
                    session.SendResponse(new MailDeleteResponse { DelIdList = session.player.MailExpireIds.ToList() }, packet.Id);
                    return;
                }
            }
            session.SendResponse(new MailDeleteResponse { DelIdList = session.player.MailExpireIds.Concat(removed.Select(entry => entry.Mail.Id)).ToList() }, packet.Id);
        }

        [RequestPacketHandler("MailGetSingleRewardRequest")]
        public static void MailGetSingleRewardRequestHandler(Session session, Packet.Request packet)
        {
            MailGetSingleRewardRequest request = packet.Deserialize<MailGetSingleRewardRequest>();
            Claim(session, packet.Id, [request.Id], true);
        }

        [RequestPacketHandler("MailGetRewardRequest")]
        public static void MailGetRewardRequestHandler(Session session, Packet.Request packet)
        {
            MailGetRewardRequest request = packet.Deserialize<MailGetRewardRequest>();
            Claim(session, packet.Id, request.IdList, false);
        }

        private static void Claim(Session session, int packetId, IReadOnlyList<string> ids, bool single)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (ReconcileExpiry(session.player, now))
            {
                try { session.player.SaveChecked(); }
                catch { SendClaimResponse(session, packetId, single, Fail, null, []); return; }
            }
            Dictionary<string, int> status = new();
            List<PlayerMail> mails = [];
            int code = ids.Count == 0 ? Empty : 0;
            foreach (string id in ids.Distinct(StringComparer.Ordinal))
            {
                PlayerMail? mail = Find(session.player, id);
                int validation = Validate(mail, now);
                if (validation != 0) { code = code == 0 ? validation : code; continue; }
                if (mail!.Status == 3) { code = code == 0 ? Repeat : code; status[id] = 3; continue; }
                if (mail.RewardGoodsList is not { Count: > 0 }) { code = code == 0 ? ExpiredOrEmpty : code; continue; }
                mails.Add(mail);
            }
            if (mails.Count == 0)
            {
                SendClaimResponse(session, packetId, single, code == 0 ? ExpiredOrEmpty : code, null, status);
                return;
            }
            List<RewardGrant> grants = [];
            foreach (PlayerMail mail in mails)
            {
                List<RewardGoodsTable> goods = [];
                foreach (PlayerMailRewardGoods snapshot in mail.RewardGoodsList!)
                {
                    if (!TryProjectReward(snapshot, out RewardGoodsTable? table))
                    {
                        SendClaimResponse(session, packetId, single, MissingReward, null, status);
                        return;
                    }
                    goods.Add(table!);
                }
                grants.Add(new RewardGrant($"mail:{mail.Id}", goods));
            }
            List<RewardGoodsTable> pendingGoods = grants
                .Where(grant => !session.inventory.AppliedRewardClaims.Contains(grant.ClaimKey, StringComparer.Ordinal))
                .SelectMany(grant => grant.Goods).ToList();
            if (!HasItemCapacity(session, pendingGoods))
            {
                SendClaimResponse(session, packetId, single, 20027011, null, status);
                return;
            }
            Dictionary<PlayerMail, int> originalStatuses = mails.ToDictionary(mail => mail, mail => mail.Status);
            try
            {
                RewardApplicationResult result = RewardHandler.ApplyRewardsOnceAndPersist(grants, session);
                foreach (PlayerMail mail in mails) { mail.Status = 3; status[mail.Id] = 3; }
                try { session.player.SaveChecked(); }
                catch
                {
                    foreach ((PlayerMail mail, int originalStatus) in originalStatuses) mail.Status = originalStatus;
                    SendClaimResponse(session, packetId, single, Fail, null, status);
                    return;
                }
                result.SendPushes(session);
                SendClaimResponse(session, packetId, single, code, mails.SelectMany(mail => mail.RewardGoodsList!).Select(ToMailGoods).ToList(), status);
            }
            catch (InvalidDataException)
            {
                SendClaimResponse(session, packetId, single, MissingReward, null, status);
            }
            catch
            {
                SendClaimResponse(session, packetId, single, Fail, null, status);
            }
        }

        private static void SendClaimResponse(Session session, int packetId, bool single, int code, List<MailRewardGoods>? goods, Dictionary<string, int> status)
        {
            if (single)
                session.SendResponse(new MailGetSingleRewardResponse { Code = code, RewardGoodsList = goods, Status = status.Values.FirstOrDefault() }, packetId);
            else
                session.SendResponse(new MailGetRewardResponse { Code = code, RewardGoodsList = goods, MailStatus = status }, packetId);
        }

        private static int ValidateRead(PlayerMail? mail, long now)
        {
            if (mail is null) return NotExist;
            if (mail.Status == 4) return Deleted;
            if (mail.SendTime > now) return NotEffect;
            return 0;
        }

        private static int Validate(PlayerMail? mail, long now)
        {
            int code = ValidateRead(mail, now);
            if (code != 0) return code;
            return mail!.ExpireTime > 0 && now > mail.ExpireTime
                ? mail.ReserveTime >= now ? Expired : ExpiredOrEmpty
                : 0;
        }

        private static bool IsVisible(PlayerMail mail, long now) =>
            mail.SendTime <= now && mail.Status is 0 or 1 or 3;
        private static PlayerMail? Find(Player player, string? id) =>
            string.IsNullOrWhiteSpace(id) ? null : player.Mails.FirstOrDefault(mail => mail.Id == id);

        internal static bool ReconcileExpiry(Player player, long now)
        {
            bool changed = false;
            foreach (PlayerMail mail in player.Mails.Where(mail => mail.ExpireTime > 0 && now > mail.ExpireTime && mail.ReserveTime < now).ToList())
            {
                player.Mails.Remove(mail);
                if (!player.MailExpireIds.Contains(mail.Id))
                    player.MailExpireIds.Add(mail.Id);
                changed = true;
            }
            return changed;
        }

        internal static List<PlayerMailRewardGoods>? ResolveMailRewards(int mailRewardId)
        {
            MailRewardTable? reward = TableReaderV2.Parse<MailRewardTable>()
                .FirstOrDefault(row => row.Id == mailRewardId);
            List<int> rewardIds = reward?.RewardIds.Where(id => id > 0).ToList() ?? [];
            if (reward is null || rewardIds.Count == 0)
                return null;
            Dictionary<int, MailRewardGoodsTable> goodsById = TableReaderV2.Parse<MailRewardGoodsTable>()
                .ToDictionary(row => row.Id);
            List<PlayerMailRewardGoods> snapshot = [];
            foreach (int rewardId in rewardIds)
            {
                if (!goodsById.TryGetValue(rewardId, out MailRewardGoodsTable? goods))
                    return null;
                RewardGoodsTable table = new() { Id = goods.Id, TemplateId = goods.TemplateId, Count = goods.Count };
                RewardType? type = RewardHandler.GetRewardType(table);
                if (type is null)
                    return null;
                PlayerMailRewardGoods entry = new()
                {
                    Id = goods.Id,
                    RewardType = (int)type,
                    TemplateId = (uint)goods.TemplateId,
                    Count = goods.Count
                };
                if (type == RewardType.Equip)
                {
                    entry.Level = goods.Params.Count > 0 ? goods.Params[0] : 0;
                    entry.Breakthrough = goods.Params.Count > 2 ? goods.Params[2] : 0;
                }
                else if (type == RewardType.Character)
                {
                    entry.Level = goods.Params.Count > 0 ? goods.Params[0] : 0;
                    entry.Quality = goods.Params.Count > 1 ? goods.Params[1] : 0;
                    entry.Grade = goods.Params.Count > 2 ? goods.Params[2] : 0;
                }
                if (!TryProjectReward(entry, out _))
                    return null;
                snapshot.Add(entry);
            }
            return snapshot.Count == 0 ? null : snapshot;
        }

        private static bool TryProjectReward(PlayerMailRewardGoods snapshot, out RewardGoodsTable? table)
        {
            table = null;
            if (snapshot.Count <= 0 || snapshot.TemplateId > int.MaxValue)
                return false;
            RewardGoodsTable candidate = new()
            {
                Id = snapshot.Id,
                TemplateId = (int)snapshot.TemplateId,
                Count = snapshot.Count
            };
            RewardType? type = RewardHandler.GetRewardType(candidate);
            if (type is null || snapshot.RewardType != (int)type)
                return false;
            bool supported = type switch
            {
                RewardType.Item => Inventory.IsValidClientItemId(candidate.TemplateId),
                RewardType.Character => candidate.Count == 1
                    && TableReaderV2.Parse<CharacterTable>().Any(row => row.Id == candidate.TemplateId),
                RewardType.Equip => candidate.Count == 1
                    && TableReaderV2.Parse<EquipTable>().Any(row => row.Id == candidate.TemplateId),
                RewardType.Fashion => candidate.Count == 1
                    && TableReaderV2.Parse<FashionTable>().Any(row => row.Id == candidate.TemplateId),
                RewardType.FashionColor => candidate.Count == 1
                    && TableReaderV2.Parse<FashionColorTable>().Any(row => row.Id == candidate.TemplateId),
                _ => false
            };
            if (!supported)
                return false;
            table = candidate;
            return true;
        }

        private static bool HasItemCapacity(Session session, IEnumerable<RewardGoodsTable> goods)
        {
            foreach (IGrouping<int, RewardGoodsTable> group in goods
                         .Where(goods => RewardHandler.GetRewardType(goods) == RewardType.Item)
                         .GroupBy(goods => goods.TemplateId))
            {
                ItemTable? table = TableReaderV2.Parse<ItemTable>().FirstOrDefault(item => item.Id == group.Key);
                if (table is null || !Inventory.IsValidClientItemId(group.Key))
                    return false;
                long current = session.inventory.Items.FirstOrDefault(item => item.Id == group.Key)?.Count ?? 0;
                if (current + group.Sum(goods => (long)goods.Count) > Inventory.GetMaxCount(table))
                    return false;
            }
            return true;
        }

        private static NotifyMails.NotifyMailsNewMailList ToNotify(PlayerMail mail) => new()
        {
            Id = mail.Id, GroupId = mail.GroupId, BatchId = mail.BatchId, Type = mail.Type, Status = mail.Status,
            SendName = mail.SendName, Title = mail.Title, Content = mail.Content, CreateTime = mail.CreateTime,
            SendTime = mail.SendTime, ExpireTime = mail.ExpireTime,
            RewardGoodsList = mail.RewardGoodsList?.Select(ToMailGoods).ToList(), IsForbidDelete = mail.IsForbidDelete,
            IsSurvey = mail.IsSurvey, ReserveTime = mail.ReserveTime
        };
        private static MailRewardGoods ToMailGoods(PlayerMailRewardGoods goods) => new() { RewardType = goods.RewardType, TemplateId = goods.TemplateId, Count = goods.Count, Level = goods.Level, Quality = goods.Quality, Grade = goods.Grade, Breakthrough = goods.Breakthrough, ConvertFrom = goods.ConvertFrom, IsGift = goods.IsGift, RewardMulti = goods.RewardMulti, Id = goods.Id };
        private static MailRewardGoods ToMailGoods(RewardGoods goods) => new() { RewardType = goods.RewardType, TemplateId = (uint)goods.TemplateId, Count = goods.Count, Level = goods.Level, Quality = goods.Quality, Grade = goods.Grade, Breakthrough = goods.Breakthrough, ConvertFrom = goods.ConvertFrom, IsGift = goods.IsGift, RewardMulti = goods.RewardMulti, Id = goods.Id };
        private static RewardGoodsTable ToTable(PlayerMailRewardGoods goods) => new() { Id = goods.Id, TemplateId = (int)goods.TemplateId, Count = goods.Count };
    }
}
