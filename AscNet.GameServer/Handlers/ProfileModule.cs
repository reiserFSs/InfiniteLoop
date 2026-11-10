using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using MessagePack;
using MongoDB.Driver;

namespace AscNet.GameServer.Handlers;

internal static class ProfileModule
{
    internal static void Respond(Session session, Packet.Request packet, int code) => session.SendResponse(packet.Name[..^7] + "Response", MessagePackSerializer.Serialize(new ProfileResult { Code = code }), packet.Id);
    internal static Player? Target(Session session, long id) => id == session.player.PlayerData.Id ? session.player : Server.Instance.SessionFromUID(id)?.player ?? Player.TryFromPlayerId(id);
    internal static Character? Characters(Session session, Player player) => player.PlayerData.Id == session.player.PlayerData.Id ? session.character : Server.Instance.SessionFromUID(player.PlayerData.Id)?.character ?? Character.collection.Find(x => x.Uid == player.PlayerData.Id).FirstOrDefault();
    internal static bool Visible(Player viewer, Player target, long setting) => viewer.PlayerData.Id == target.PlayerData.Id || setting == 1 || setting == 2 && target.Social.Friends.ContainsKey(checked((uint)viewer.PlayerData.Id));
    internal static int SetAppearance(Player player, Character characters, SetAppearanceRequest request)
    {
        var setting = request.AppearanceSettingInfo;
        if (setting is null || request.Characters is null || request.CharactersAppearanceType is < 0 or > 1 || new[] { setting.TitleType, setting.CharacterType, setting.FashionType, setting.WeaponFashionType, setting.DormitoryType }.Any(x => x is < 1 or > 3) || request.Characters.Count != request.Characters.Distinct().Count()) return 5;
        if (request.Characters.Any(id => !characters.Characters.Any(c => c.Id == id))) return 20000003;
        if (setting.DormitoryId != 0 && !player.Dorm.Rooms.Any(r => r.Id == setting.DormitoryId)) return 5;
        player.PlayerData.AppearanceShowType = request.CharactersAppearanceType;
        player.PlayerData.ShowCharacters = request.Characters.ToList();
        player.PlayerData.AppearanceSettingInfo = setting;
        return 0;
    }
    internal static Dictionary<string, object?> Detail(Player player, Character? characters, Player viewer)
    {
        var p = player.PlayerData;
        return new()
        {
            ["Id"] = p.Id, ["Name"] = p.Name, ["Level"] = p.Level, ["Sign"] = p.Sign,
            ["Birthday"] = p.Birthday, ["Gender"] = p.Gender, ["HonorLevel"] = p.HonorLevel,
            ["Likes"] = p.Likes, ["CurrHeadPortraitId"] = p.CurrHeadPortraitId, ["CurrHeadFrameId"] = p.CurrHeadFrameId,
            ["CurrMedalId"] = p.CurrMedalId, ["DisplayCharId"] = p.DisplayCharId, ["DisplayCharIdList"] = p.DisplayCharIdList,
            ["AppearanceShowType"] = p.AppearanceShowType, ["AppearanceSettingInfo"] = p.AppearanceSettingInfo,
            ["LastLoginTime"] = p.LastLoginTime, ["CreateTime"] = p.CreateTime,
            ["MedalInfos"] = player.UnlockedMedals.Where(_ => Visible(viewer, player, p.AppearanceSettingInfo.TitleType)).Select(m => new NotifyMedalData.NotifyMedalDataMedalInfo { Id = m.Id, Time = m.Time, KeepTime = m.KeepTime }).ToList(),
            ["AchievementDetail"] = new Dictionary<string, object>
            {
                ["Achievement"] = AscNet.Common.Util.TableReaderV2.Parse<AscNet.Table.V2.share.task.TaskTable>().Count(t => t.Type == 4 && player.MissionProgress.ClaimedTaskIds.Contains(t.Id)),
                ["TotalAchievement"] = AscNet.Common.Util.TableReaderV2.Parse<AscNet.Table.V2.share.task.TaskTable>().Count(t => t.Type == 4)
            },
            ["AssistCharacterDetail"] = Visible(viewer, player, p.AppearanceSettingInfo.CharacterType) ? characters?.Characters.FirstOrDefault(c => c.Id == player.AssistCharacterId) : null,
            ["DormDetail"] = Visible(viewer, player, p.AppearanceSettingInfo.DormitoryType) && player.Dorm.Rooms.FirstOrDefault(r => r.Id == p.AppearanceSettingInfo.DormitoryId) is { } room ? new Dictionary<string, object> { ["DormitoryId"] = room.Id, ["DormitoryName"] = room.Name } : null,
            // XUiPlayerInfoBase:104 passes any nonzero value to XUiPanelNameplate; a missing key is nil ~= 0 and aborts the screen.
            ["CurrentWearNameplate"] = characters?.GetCurrentWearNameplate(DateTimeOffset.UtcNow.ToUnixTimeSeconds()) ?? 0,
            // XExhibitionManager reads other players' collection progress from this list (pairs(nil) throws).
            ["GatherIds"] = player.GatherRewards,
            ["GuildDetail"] = GuildModule.FindMembership(p.Id) is { } guild
                ? new Dictionary<string, object> { ["GuildId"] = guild.Id, ["GuildName"] = guild.Name, ["GuildRankLevel"] = GuildModule.Rank(guild, p.Id) }
                : null,
            // The mentor system is not implemented (Docs/closed.md), so every player has no mentor identity.
            ["MentorDetail"] = new Dictionary<string, object> { ["MentorType"] = 0, ["MentorId"] = 0 }
        };
    }
    [RequestPacketHandler("SetAppearanceRequest")]
    public static void SetAppearanceHandler(Session s, Packet.Request p)
    {
        int code = SetAppearance(s.player, s.character, p.Deserialize<SetAppearanceRequest>());
        if (code == 0) s.player.Save();
        s.SendResponse(new SetAppearanceResponse { Code = code }, p.Id);
    }
    [RequestPacketHandler("QueryPlayerDetailRequest")]
    public static void DetailHandler(Session s, Packet.Request p)
    {
        var target = Target(s, p.Deserialize<QueryPlayerDetailRequest>().PlayerId);
        s.SendResponse(new QueryPlayerDetailResponse { Code = target is null ? 20037001 : 0, Detail = target is null ? null : Detail(target, Characters(s, target), s.player) }, p.Id);
    }
    [RequestPacketHandler("QueryPlayerCharacterListRequest")]
    public static void CharacterListHandler(Session s, Packet.Request p)
    {
        var target = Target(s, p.Deserialize<ProfilePlayerRequest>().PlayerId);
        var response = new QueryPlayerCharacterListResponse { Code = target is null ? 20037001 : !Visible(s.player, target, target.PlayerData.AppearanceSettingInfo.CharacterType) ? 20037002 : 0 };
        if (response.Code == 0 && target is not null) response.CharacterShows = (Characters(s, target)?.Characters ?? []).Where(c => target.PlayerData.AppearanceShowType == 0 || target.PlayerData.ShowCharacters.Contains(c.Id)).ToList();
        s.SendResponse(response, p.Id);
    }
    [RequestPacketHandler("QueryPlayerFashionRequest")]
    public static void FashionHandler(Session s, Packet.Request p)
    {
        var target = Target(s, p.Deserialize<ProfilePlayerRequest>().PlayerId);
        var response = new QueryPlayerFashionResponse { Code = target is null ? 20037001 : !Visible(s.player, target, target.PlayerData.AppearanceSettingInfo.FashionType) ? 20037002 : 0 };
        if (response.Code == 0 && target is not null && Characters(s, target) is { } c) { response.Fashions = c.Fashions.Select(f => f.Id).ToList(); response.FashionColors = c.FashionColors; }
        s.SendResponse(response, p.Id);
    }
    [RequestPacketHandler("QueryPlayerWeaponFashionRequest")]
    public static void WeaponFashionHandler(Session s, Packet.Request p)
    {
        var target = Target(s, p.Deserialize<ProfilePlayerRequest>().PlayerId);
        var response = new QueryPlayerWeaponFashionResponse { Code = target is null ? 20037001 : !Visible(s.player, target, target.PlayerData.AppearanceSettingInfo.WeaponFashionType) ? 20037002 : 0 };
        if (response.Code == 0 && target is not null) response.WeaponFashions = (Characters(s, target)?.WeaponFashions ?? []).Select(f => f.Id).ToList();
        s.SendResponse(response, p.Id);
    }
    [RequestPacketHandler("QueryPlayerTitleRequest")]
    public static void TitleHandler(Session s, Packet.Request p)
    {
        var target = Target(s, p.Deserialize<ProfilePlayerRequest>().PlayerId);
        var response = new QueryPlayerTitleResponse { Code = target is null ? 20037001 : !Visible(s.player, target, target.PlayerData.AppearanceSettingInfo.TitleType) ? 20037002 : 0 };
        if (response.Code == 0 && target is not null)
        {
            bool self = target.PlayerData.Id == s.player.PlayerData.Id;
            var metadata = AscNet.Common.Util.TableReaderV2.Parse<AscNet.Table.V2.share.scoretitle.ScoreTitleTable>().ToDictionary(x => x.Id);
            response.Titles = (Characters(s, target)?.ScoreTitles ?? []).Where(t => self || !target.Profile.HideCollection && (!metadata.TryGetValue((int)t.Id, out var row) || !target.Profile.HiddenTitleTypes.Contains(row.Type))).ToList();
            ProfileShowcaseModule.EnsureWalls(target);
            response.Walls = target.Profile.Walls.Where(w => self || !target.Profile.HideCollection && w.IsShow).Select(ProfileShowcaseModule.WireWall).ToList();
        }
        s.SendResponse(response, p.Id);
    }
    [RequestPacketHandler("QueryCharacterInfoRequest")]
    public static void CharacterInfoHandler(Session s, Packet.Request p)
    {
        var request = p.Deserialize<ProfilePlayerRequest>();
        var target = Target(s, request.PlayerId);
        var response = new QueryCharacterInfoResponse { Code = target is null ? 20037001 : !Visible(s.player, target, target.PlayerData.AppearanceSettingInfo.CharacterType) ? 20037002 : target.PlayerData.AppearanceShowType != 0 && !target.PlayerData.ShowCharacters.Contains(request.CharacterId) ? 20037003 : 0 };
        if (response.Code == 0 && target is not null)
        {
            var c = Characters(s, target);
            response.CharacterData = c?.Characters.FirstOrDefault(x => x.Id == request.CharacterId);
            if (response.CharacterData is null) response.Code = 20037004;
            else if (c is not null)
            {
                response.EquipData = c.Equips.Where(x => x.CharacterId == request.CharacterId).ToList();
                response.PartnerData = c.Partners.FirstOrDefault(x => x.CharacterId == request.CharacterId);
                response.WeaponFashionId = c.WeaponFashions.FirstOrDefault(x => x.UseCharacterList.Contains(checked((int)request.CharacterId)))?.Id ?? 0;
                response.AssignChapterRecords = target.Assign.Chapters.Select(record => (object)new AssignChapterInfo { ChapterId = record.ChapterId, CharacterId = record.CharacterId, IsGetReward = record.IsGetReward }).ToList();
                response.AwarenessSetPositions = AwarenessModule.SetPositions(target);
            }
        }
        s.SendResponse(response, p.Id);
    }
}
