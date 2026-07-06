using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.character;
using AscNet.Table.V2.share.fashion;
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
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    #endregion

    internal class FashionModule
    {
        [RequestPacketHandler("FashionUseRequest")]
        public static void HandleFashionUseRequestHandler(Session session, Packet.Request packet)
        {
            FashionUseRequest req = packet.Deserialize<FashionUseRequest>();
            var character = session.character.Characters.Find(x => x.Id == TableReaderV2.Parse<FashionTable>().Find(x => x.Id == req.FashionId)?.CharacterId);

            if (character is not null)
            {
                character.FashionId = req.FashionId;

                NotifyCharacterDataList notifyCharacterData = new();
                notifyCharacterData.CharacterDataList.Add(character);
                session.SendPush(notifyCharacterData);
            }

            session.SendResponse(new FashionUseResponse(), packet.Id);
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
            session.log.Info($"[AWAKEN-PROBE] FashionUnLockRequest fashionId={req.FashionId} tableFound={fashionRow is not null} characterId={fashionRow?.CharacterId.ToString() ?? "<null>"} characterDefaultFashion={characterRow?.DefaultNpcFashtionId.ToString() ?? "<null>"} isAwakenFashion={isAwakenFashion} ownsCharacter={ownsCharacter} existingFashion={fashion is not null} existingLocked={fashion?.IsLock.ToString() ?? "<null>"}");
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
            else if (fashion is not null && fashion.IsLock)
            {
                fashion.IsLock = false;
                changed = true;
            }

            if (changed && fashion is not null)
            {
                FashionSyncNotify fashionSync = new();
                fashionSync.FashionList.Add(fashion);
                session.SendPush(fashionSync);
                session.character.Save();
                session.log.Info($"[AWAKEN-PROBE] FashionUnLockChanged fashionId={req.FashionId} pushed=True saved=True");
            }

            session.log.Info($"[AWAKEN-PROBE] FashionUnLockResponse fashionId={req.FashionId} changed={changed} finalFashionPresent={fashion is not null} finalLocked={fashion?.IsLock.ToString() ?? "<null>"}");
            session.SendResponse(new FashionUnLockResponse(), packet.Id);
        }
    }
}
