using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.character;
using AscNet.Table.V2.share.fashion;
using AscNet.Table.V2.share.reward;
using MessagePack;

namespace AscNet.GameServer.Handlers
{
    #region MsgPackScheme
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    [MessagePackObject(true)]
    public class FashionUnLockRequest
    {
        public uint FashionId { get; set; }
    }

    [MessagePackObject(true)]
    public class FashionUnLockResponse
    {
        public int Code { get; set; }
    }

    [MessagePackObject(true)]
    public class FashionUseRequest
    {
        public uint FashionId { get; set; }
    }

    [MessagePackObject(true)]
    public class FashionUseResponse
    {
        public int Code { get; set; }
    }

    [MessagePackObject(true)]
    public class WeaponFashionUseRequest
    {
        public int Id { get; set; }
        public uint CharacterId { get; set; }
    }

    [MessagePackObject(true)]
    public class WeaponFashionUseResponse
    {
        public int Code { get; set; }
    }

    [MessagePackObject(true)]
    public class FashionRandomActiveRequest
    {
        public uint CharacterId { get; set; }
        public bool Enable { get; set; }
    }

    [MessagePackObject(true)]
    public class FashionRandomActiveResponse
    {
        public int Code { get; set; }
    }

    [MessagePackObject(true)]
    public class FashionSwitchColorRequest
    {
        public uint FashionId { get; set; }
        public int ColorId { get; set; }
    }

    [MessagePackObject(true)]
    public class FashionSwitchColorResponse
    {
        public int Code { get; set; }
    }
    [MessagePackObject(true)]
    public class FashionSuitPoolSaveRequest
    {
        public uint CharacterId { get; set; }
        public Dictionary<int, int> FashionSuits { get; set; }
        public List<int> ActiveIds { get; set; }
    }

    [MessagePackObject(true)]
    public class FashionSuitPoolSaveResponse
    {
        public int Code { get; set; }
    }

    [MessagePackObject(true)]
    public class FashionGetSuitRewardRequest
    {
        public int SuitId { get; set; }
    }

    [MessagePackObject(true)]
    public class FashionGetSuitRewardResponse
    {
        public int Code { get; set; }
        public List<RewardGoods> RewardGoodsList { get; set; } = new();
    }

#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    #endregion

    internal class FashionModule
    {
        [RequestPacketHandler("FashionRandomActiveRequest")]
        public static void HandleFashionRandomActiveRequest(Session session, Packet.Request packet)
        {
            FashionRandomActiveRequest request = packet.Deserialize<FashionRandomActiveRequest>();
            CharacterData? character = session.character.Characters.Find(candidate => candidate.Id == request.CharacterId);

            if (character is null)
            {
                session.SendResponse(new FashionRandomActiveResponse { Code = 20009001 }, packet.Id);
                return;
            }

            if (character.RandomFashion != request.Enable)
            {
                character.RandomFashion = request.Enable;
                session.character.Save();
            }

            NotifyCharacterDataList notifyCharacterData = new();
            notifyCharacterData.CharacterDataList.Add(character);
            session.SendPush(notifyCharacterData);
            session.SendResponse(new FashionRandomActiveResponse(), packet.Id);
        }

        [RequestPacketHandler("FashionSuitPoolSaveRequest")]
        public static void HandleFashionSuitPoolSaveRequest(Session session, Packet.Request packet)
        {
            const int invalidRequestCode = 20012001;
            FashionSuitPoolSaveRequest request = packet.Deserialize<FashionSuitPoolSaveRequest>();

            if (!session.character.Characters.Any(candidate => candidate.Id == request.CharacterId))
            {
                session.SendResponse(new FashionSuitPoolSaveResponse { Code = 20009001 }, packet.Id);
                return;
            }

            if (request.FashionSuits is null || request.FashionSuits.Count == 0
                || request.ActiveIds is null || request.ActiveIds.Count == 0)
            {
                session.SendResponse(new FashionSuitPoolSaveResponse { Code = invalidRequestCode }, packet.Id);
                return;
            }

            HashSet<int> activeIds = new(request.ActiveIds.Count);
            foreach (int fashionId in request.ActiveIds)
            {
                if (!activeIds.Add(fashionId) || !request.FashionSuits.ContainsKey(fashionId))
                {
                    session.SendResponse(new FashionSuitPoolSaveResponse { Code = invalidRequestCode }, packet.Id);
                    return;
                }
            }

            var fashionRows = TableReaderV2.Parse<FashionTable>();
            List<FashionList> submittedFashions = new(request.FashionSuits.Count);
            foreach ((int fashionId, int weaponFashionId) in request.FashionSuits)
            {
                FashionList? fashion = fashionId > 0
                    ? session.character.Fashions.Find(candidate =>
                        candidate.Id == fashionId && !candidate.IsLock)
                    : null;
                FashionTable? fashionRow = fashionId > 0
                    ? fashionRows.Find(candidate => candidate.Id == fashionId)
                    : null;

                if (weaponFashionId < 0 || fashion is null || fashionRow is null
                    || fashionRow.CharacterId != request.CharacterId)
                {
                    session.SendResponse(new FashionSuitPoolSaveResponse { Code = invalidRequestCode }, packet.Id);
                    return;
                }

                submittedFashions.Add(fashion);
            }

            submittedFashions.Sort((left, right) => left.Id.CompareTo(right.Id));
            bool changed = false;
            foreach (FashionList fashion in submittedFashions)
            {
                bool isRandom = activeIds.Contains((int)fashion.Id);
                int weaponFashionId = request.FashionSuits[(int)fashion.Id];
                if (fashion.IsRandom != isRandom || fashion.WeaponFashionId != weaponFashionId)
                {
                    fashion.IsRandom = isRandom;
                    fashion.WeaponFashionId = weaponFashionId;
                    changed = true;
                }
            }

            if (changed)
                session.character.Save();

            session.SendPush(new FashionSyncNotify { FashionList = submittedFashions });
            session.SendResponse(new FashionSuitPoolSaveResponse(), packet.Id);
        }

        [RequestPacketHandler("FashionUseRequest")]
        public static void HandleFashionUseRequestHandler(Session session, Packet.Request packet)
        {
            const int invalidRequestCode = 20012001;
            FashionUseRequest request = packet.Deserialize<FashionUseRequest>();
            FashionTable? fashionRow = TableReaderV2.Parse<FashionTable>().Find(candidate => candidate.Id == request.FashionId);
            CharacterData? character = fashionRow is not null
                ? session.character.Characters.Find(candidate => candidate.Id == fashionRow.CharacterId)
                : null;
            FashionList? ownedFashion = request.FashionId > 0
                ? session.character.Fashions.Find(candidate => candidate.Id == request.FashionId && !candidate.IsLock)
                : null;

            // The equipped body coating must be an unlocked fashion belonging to an owned character.
            if (character is null || ownedFashion is null)
            {
                session.SendResponse(new FashionUseResponse { Code = invalidRequestCode }, packet.Id);
                return;
            }

            if (character.FashionId != request.FashionId)
            {
                character.FashionId = request.FashionId;
                session.character.Save();
            }

            session.SendPush(new NotifyCharacterDataList { CharacterDataList = { character } });
            session.SendResponse(new FashionUseResponse(), packet.Id);
        }

        [RequestPacketHandler("WeaponFashionUseRequest")]
        public static void HandleWeaponFashionUseRequest(Session session, Packet.Request packet)
        {
            const int invalidRequestCode = 20012001;
            WeaponFashionUseRequest request = packet.Deserialize<WeaponFashionUseRequest>();

            if (!session.character.Characters.Any(character => character.Id == request.CharacterId))
            {
                session.SendResponse(new WeaponFashionUseResponse { Code = 20009001 }, packet.Id);
                return;
            }

            WeaponFashionData? target = null;
            if (request.Id != 0)
            {
                target = session.character.WeaponFashions.Find(fashion => fashion.Id == request.Id);
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                if (target is null || (target.ExpireTime != 0 && target.ExpireTime <= now))
                {
                    session.SendResponse(new WeaponFashionUseResponse { Code = invalidRequestCode }, packet.Id);
                    return;
                }
            }

            int characterId = (int)request.CharacterId;
            List<WeaponFashionData>? changedWeaponFashions = null;
            foreach (WeaponFashionData fashion in session.character.WeaponFashions)
            {
                bool changed = false;
                bool isTarget = ReferenceEquals(fashion, target);
                int retainedIndex = isTarget
                    ? fashion.UseCharacterList.IndexOf(characterId)
                    : -1;
                for (int index = fashion.UseCharacterList.Count - 1; index >= 0; index--)
                {
                    if (fashion.UseCharacterList[index] == characterId && index != retainedIndex)
                    {
                        fashion.UseCharacterList.RemoveAt(index);
                        changed = true;
                    }
                }

                if (isTarget && retainedIndex < 0)
                {
                    fashion.UseCharacterList.Add(characterId);
                    changed = true;
                }

                if (changed)
                {
                    changedWeaponFashions ??= new();
                    changedWeaponFashions.Add(fashion);
                }
            }

            if (changedWeaponFashions is not null)
            {
                session.character.Save();
                session.SendPush(new NotifyWeaponFashionInfo
                {
                    WeaponFashionDataList = changedWeaponFashions
                });
            }

            session.SendResponse(new WeaponFashionUseResponse(), packet.Id);
        }

        [RequestPacketHandler("FashionSwitchColorRequest")]
        public static void HandleFashionSwitchColorRequest(Session session, Packet.Request packet)
        {
            const int invalidRequestCode = 20012001;
            FashionSwitchColorRequest request = packet.Deserialize<FashionSwitchColorRequest>();
            FashionList? fashion = session.character.Fashions.Find(candidate =>
                candidate.Id == request.FashionId && !candidate.IsLock);
            FashionTable? fashionRow = TableReaderV2.Parse<FashionTable>()
                .Find(candidate => candidate.Id == request.FashionId);
            FashionColorTable? colorRow = request.ColorId == 0
                ? null
                : TableReaderV2.Parse<FashionColorTable>().Find(candidate =>
                    candidate.Id == request.ColorId
                    && candidate.OriginalFashionId == request.FashionId);
            bool ownsColor = request.ColorId == 0
                || (session.character.FashionColors?.TryGetValue(
                        (int)request.FashionId,
                        out List<int>? ownedColorIds) == true
                    && ownedColorIds.Contains(request.ColorId));

            if (fashion is null
                || fashionRow is null
                || (request.ColorId != 0 && (colorRow is null || !ownsColor)))
            {
                session.SendResponse(new FashionSwitchColorResponse { Code = invalidRequestCode }, packet.Id);
                return;
            }

            if (fashion.ColorId != request.ColorId)
            {
                fashion.ColorId = request.ColorId;
                session.character.Save();
            }

            List<int> ownedColors = session.character.FashionColors?
                .GetValueOrDefault((int)request.FashionId)?
                .Where(colorId => TableReaderV2.Parse<FashionColorTable>().Any(candidate =>
                    candidate.Id == colorId
                    && candidate.OriginalFashionId == request.FashionId))
                .Distinct()
                .Order()
                .ToList() ?? [];
            session.SendPush(new FashionSyncNotify
            {
                FashionList = [fashion],
                FashionColors = ownedColors.Count == 0
                    ? []
                    : new Dictionary<int, List<int>> { [(int)request.FashionId] = ownedColors }
            });
            session.SendResponse(new FashionSwitchColorResponse(), packet.Id);
        }

        [RequestPacketHandler("FashionUnLockRequest")]
        public static void HandleFashionUnLockRequestHandler(Session session, Packet.Request packet)
        {
            FashionUnLockRequest req = packet.Deserialize<FashionUnLockRequest>();
            FashionTable? fashionRow = TableReaderV2.Parse<FashionTable>().Find(x => x.Id == req.FashionId);
            CharacterTable? characterRow = fashionRow is not null
                ? TableReaderV2.Parse<CharacterTable>().Find(x => x.Id == fashionRow.CharacterId)
                : null;
            bool isAwakenFashion = characterRow?.DefaultNpcFashtionId > 0
                && (req.FashionId == (uint)(characterRow.DefaultNpcFashtionId + 1)
                    || req.FashionId == (uint)(characterRow.DefaultNpcFashtionId + 2));
            bool ownsCharacter = isAwakenFashion && session.character.Characters.Any(x => x.Id == fashionRow!.CharacterId);
            var fashion = session.character.Fashions.Find(x => x.Id == req.FashionId);
            bool changed = false;

            if (fashion is null && ownsCharacter)
            {
                fashion = new FashionList
                {
                    Id = req.FashionId,
                    IsLock = false
                };
                session.character.Fashions.Add(fashion);
                changed = true;
            }
            else if (fashion is not null && fashion.IsLock && ownsCharacter)
            {
                fashion.IsLock = false;
                changed = true;
            }

            if (changed && fashion is not null)
            {
                FashionSyncNotify fashionSync = new();
                fashionSync.FashionList.Add(fashion);
                foreach (int colorId in TableReaderV2.Parse<FashionColorTable>()
                             .Where(color => color.OriginalFashionId == fashion.Id)
                             .Select(color => color.Id))
                {
                    RewardHandler.UnlockFashionColorReward(colorId, session, fashionSync);
                }
                session.SendPush(fashionSync);
                session.character.Save();
            }

            session.SendResponse(new FashionUnLockResponse(), packet.Id);
        }

        // EN share/text/CodeText: Wardrobe Coating series failures, FashionSuit* (20010011..20010014).
        // Client gates claims locally; the server re-validates config, completeness, ownership, then claim.
        [RequestPacketHandler("FashionGetSuitRewardRequest")]
        public static void HandleFashionGetSuitRewardRequest(Session session, Packet.Request packet)
        {
            const int configNotFoundCode = 20010011; // FashionSuitConfigNotFound
            const int notCompleteCode = 20010012;    // FashionSuitIsNotComplete
            const int notGatheredCode = 20010013;    // FashionSuitIsNotGathered
            const int alreadyRewardCode = 20010014;  // FashionSuitIsAlreadyReward
            const int serverInternalErrorCode = 2;   // ServerInternalError

            FashionGetSuitRewardRequest request = packet.Deserialize<FashionGetSuitRewardRequest>();
            FashionSuitTable? suit = TableReaderV2.Parse<FashionSuitTable>()
                .Find(candidate => candidate.Id == request.SuitId);

            if (suit is null)
            {
                session.SendResponse(new FashionGetSuitRewardResponse { Code = configNotFoundCode }, packet.Id);
                return;
            }

            if (!string.Equals(suit.IsComplete, "true", StringComparison.OrdinalIgnoreCase))
            {
                session.SendResponse(new FashionGetSuitRewardResponse { Code = notCompleteCode }, packet.Id);
                return;
            }

            List<RewardGoodsTable> goods = suit.RewardId is int rewardId and > 0
                ? RewardHandler.GetRewardGoods(rewardId)
                : [];
            if (suit.FashionIds.Count == 0 || goods.Count == 0)
            {
                session.SendResponse(new FashionGetSuitRewardResponse { Code = configNotFoundCode }, packet.Id);
                return;
            }

            // Client dictionary membership only; IsLock and weapon fashion ownership are not suit requirements.
            if (suit.FashionIds.Any(fashionId =>
                    !session.character.Fashions.Any(fashion => fashion.Id == fashionId)))
            {
                session.SendResponse(new FashionGetSuitRewardResponse { Code = notGatheredCode }, packet.Id);
                return;
            }

            string claimKey = FashionSuitClaimKey(session, suit.Id);
            if (IsFashionSuitClaimed(session, claimKey))
            {
                session.SendResponse(new FashionGetSuitRewardResponse { Code = alreadyRewardCode }, packet.Id);
                return;
            }

            RewardApplicationResult result;
            try
            {
                // A single receipt is a partial save; re-running resumes the missing document idempotently.
                result = RewardHandler.ApplyRewardsOnceAndPersist(
                    [new RewardGrant(claimKey, goods)],
                    session);
            }
            catch (Exception exception)
            {
                session.log.Error($"Failed to persist fashion suit reward {suit.Id}: {exception}");
                session.SendResponse(new FashionGetSuitRewardResponse { Code = serverInternalErrorCode }, packet.Id);
                return;
            }

            result.SendPushes(session);
            session.SendResponse(new FashionGetSuitRewardResponse
            {
                RewardGoodsList = result.RewardGoods
            }, packet.Id);
        }

        // Login projection: only fully receipted suits, deterministically ordered by table ID.
        internal static List<FashionSuitData> BuildClaimedFashionSuits(Session session)
        {
            return TableReaderV2.Parse<FashionSuitTable>()
                .Where(suit => suit.Id > 0 && IsFashionSuitClaimed(session, FashionSuitClaimKey(session, suit.Id)))
                .OrderBy(suit => suit.Id)
                .Select(suit => new FashionSuitData { Id = suit.Id, IsReward = true })
                .ToList();
        }

        private static string FashionSuitClaimKey(Session session, int suitId)
            => $"fashion-suit:{session.player.PlayerData.Id}:{suitId}";

        private static bool IsFashionSuitClaimed(Session session, string claimKey)
            => session.inventory.AppliedRewardClaims.Contains(claimKey, StringComparer.Ordinal)
                && session.character.AppliedRewardClaims.Contains(claimKey, StringComparer.Ordinal);
    }
}
