using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.GameServer.Game;
using AscNet.Table.V2.share.condition;
using AscNet.Table.V2.share.functional;
using AscNet.Table.V2.share.theatre5;
using System.Globalization;

namespace AscNet.GameServer.Handlers;

internal static partial class Theatre5Module
{
    internal static int ConfigInt(string key, int index = 0) => Convert.ToInt32(
        Rows<Theatre5ConfigTable>().Single(row => row.Key == key).Values[index], CultureInfo.InvariantCulture);

    private static bool FeatureAvailable(Session session) => Rows<FunctionalOpenTable>()
        .Single(row => row.Id == 10491).Condition.Where(id => id > 0).All(id => IsConditionMet(session, id));

    internal static void EnsureAvailable(Session session, int? requiredMode = null)
    {
        Require(FeatureAvailable(session), 20280001);
        var data = session.player.Theatre5.Data;
        Require(data.ActivityId > 0 && data.PvpType is 1 or 2, 20280001);
        if (requiredMode.HasValue) Require(data.PvpType == requiredMode, 20280006);
        // Local calendar authorization is limited to 34/35/46401; no other season is opened.
        var activity = Rows<Theatre5ActivityTable>().Single(row => row.Id == data.ActivityId);
        Require(Convert.ToInt32(activity.TimeId, CultureInfo.InvariantCulture) == 46401, 20280001);
    }

    internal static void PrepareLogin(Session session)
    {
        ResumePending(session, forLogin: true);
        Mutation m = new(session);
        m.State.RequestReceipts.Clear();
        if (FeatureAvailable(session))
        {
            if (m.State.Epoch == 0) m.State.Epoch = 1;
            if (m.Data.ActivityId == 0)
                m.Data.ActivityId = Rows<Theatre5ActivityTable>().Single(row => row.TimeId == 46401).Id;
            if (m.Data.PvpType == 0) m.Data.PvpType = 2;
            // PvpCondition is authored zero for all playable rows; membership does not grant account characters.
            foreach (var character in Rows<Theatre5CharacterTable>().Where(row => row.Priority > 0))
            {
                int rating = ConfigInt("InitRating");
                m.Data.Characters.TryAdd(character.Id, new Theatre5PvpCharacter
                {
                    Id = character.Id, Rating = rating, FashionId = character.FashionIds[0],
                    RankProtectNum = RankForRating(rating).RankProtect
                });
            }
            InitializePveState(m);
            if (m.Data.PvpAdventureData is { } pvp)
            {
                m.State.PvpInitialRating ??= pvp.NormalOriginRating;
                if (pvp.Status == 8) pvp.NormalOriginRating = ProjectPvpFinish(m).Rating;
            }
            if (m.Data.PvpAdventureData is { Status: 2, ShopData: null, SkillChoiceData: null })
            {
                // InitGame and EnterShop are separate client calls. Only fresh login closes a
                // disconnected half-step; live Init must not push into the not-yet-created adventure.
                int selectedMode = m.Data.PvpType;
                m.Data.PvpType = 1;
                EnterShop(m);
                m.Data.PvpType = selectedMode;
            }
            RecoverPendingBoxes(m);
            // Fresh activity hydration replaces these transient mode pushes. Never do this on a live RPC.
            m.Pushes.Clear();
        }
        Persist(m, string.Empty, string.Empty, null);
    }

    internal static NotifyTheatre5ActivityData BuildLoginData(Session session) =>
        new() { Theatre5DataDb = Clone(session.player.Theatre5.Data) };

    internal static Theatre5AdventureData Adventure(Mutation m)
    {
        Theatre5AdventureData? adventure = m.Data.PvpType == 1 ? m.Data.PvpAdventureData : m.Data.PveAdventureData;
        Require(adventure is not null, 20280007);
        return adventure;
    }

    internal static Theatre5AdventureData InitializeAdventure(Mutation m, int mode, int characterId)
    {
        Require(mode is 1 or 2 && mode == m.Data.PvpType, 20280006);
        Require(mode == 1 ? m.Data.Characters.ContainsKey(characterId) : m.Data.PveCharacters.ContainsKey(characterId),
            mode == 1 ? 20280002 : 20282021);
        Theatre5AdventureData adventure;
        m.State.RunId = checked(m.State.RunId + 1);
        if (mode == 1)
        {
            m.State.PvpRunId = m.State.RunId;
            m.State.PvpAttempt = null;
            m.State.PvpOpponent = null;
            m.State.PvpOpponentPlayerId = m.State.PvpOpponentRobotId = m.State.PvpOpponentRating = 0;
            m.State.PvpWinCount = m.State.PvpLoseCount = m.State.PvpDrawCount = 0;
            m.State.PvpContinueWin = m.State.PvpContinueLose = 0;
            m.State.PvpProcessRating = 0;
            m.State.PvpInitialRating = m.Data.Characters[characterId].Rating;
            m.Data.PvpChooseMissionBounty.Clear();
            adventure = m.Data.PvpAdventureData = new()
            {
                NormalOriginRating = m.Data.Characters[characterId].Rating
            };
        }
        else
        {
            m.State.PveRunId = m.State.RunId;
            m.State.PveAttempt = null;
            m.Data.PveChooseMissionBounty.Clear();
            adventure = m.Data.PveAdventureData = new();
        }
        adventure.CharacterId = characterId;
        adventure.Status = 2;
        adventure.RoundNum = 1;
        adventure.Version = mode == 2 ? ConfigInt("PveVersion") : m.Data.ActivityId;
        adventure.Health = ConfigInt(mode == 1 ? "PvpHealth" : "PveHealth");
        adventure.CharacterLv = 1;
        adventure.CharacterExp = ConfigInt("CharacterInitExp");
        InitializeBag(m, adventure);
        if (mode == 1) InitializeProgression(m);
        return adventure;
    }

    [RequestPacketHandler("PveOrPvpChangeRequest")]
    public static void PveOrPvpChange(Session session, Packet.Request packet) =>
        Handle<PveOrPvpChangeRequest, PveOrPvpChangeResponse>(session, packet, (m, request, response) =>
        {
            EnsureAvailable(session);
            // Native authorization remains bound to its own run; switching never clears either run.
            m.Data.PvpType = m.Data.PvpType == 1 ? 2 : 1;
        });

    [RequestPacketHandler("Theatre5CharacterSkinSetRequest")]
    public static void Theatre5CharacterSkinSet(Session session, Packet.Request packet) =>
        Handle<Theatre5CharacterSkinSetRequest, Theatre5CharacterSkinSetResponse>(session, packet, (m, request, response) =>
        {
            EnsureAvailable(session);
            var config = Rows<Theatre5CharacterTable>().SingleOrDefault(row => row.Id == request.CharacterId && row.Priority > 0);
            Require(config is not null, 20280004);
            Require(m.Data.PvpType == 1 ? m.Data.Characters.ContainsKey(request.CharacterId)
                : m.Data.PveCharacters.ContainsKey(request.CharacterId), 20280002);
            Require(config.FashionIds.Contains(request.FashionId), 20280004);
            Require(Rows<Theatre5CharacterFashionTable>().Any(row => row.Id == request.FashionId), 20280004);
            // Theatre5 story coatings are free inside this mode; this does not grant account fashions.
            if (m.Data.PvpType == 1) m.Data.Characters[request.CharacterId].FashionId = request.FashionId;
            else m.Data.PveCharacters[request.CharacterId].FashionId = request.FashionId;
        });
}
