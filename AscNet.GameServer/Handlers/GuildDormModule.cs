using System.Security.Cryptography;
using AscNet.Common;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Game;
using AscNet.Table.V2.share.guilddorm;

namespace AscNet.GameServer.Handlers;

internal static class GuildDormModule
{
    public static GuildDormConfigTable Config => TableReaderV2.Parse<GuildDormConfigTable>().Single(row => row.Id == 1);
    public static GuildDormRoomTable Room(int id) => TableReaderV2.Parse<GuildDormRoomTable>().FirstOrDefault(row => row.Id == id)
        ?? throw new ServerCodeException("Invalid guild dorm room", 20173002);
    private static void Require(bool valid, int code) { if (!valid) throw new ServerCodeException("Guild dorm request rejected", code); }
    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    public static bool IsTimeOpen(int? timeId) => timeId is null or <= 0 || ActivityScheduleService.IsOpen(timeId.Value, DateTimeOffset.UtcNow);

    public static GuildDormRoomThemeTable Theme(Guild guild, int roomId)
    {
        Room(roomId);
        var themes = TableReaderV2.Parse<GuildDormRoomThemeTable>().Where(row => row.RoomId == roomId);
        if (guild.DormRooms.TryGetValue(roomId, out var state))
            return themes.FirstOrDefault(row => row.Id == state.ThemeId) ?? throw new ServerCodeException("Invalid persisted dorm theme", 20173021);
        return themes.Where(row => (row.NeedBuy is null or 0) && IsTimeOpen(row.TimeId)).OrderBy(row => row.Index).First();
    }

    private static GuildDormRoomState RoomState(Guild guild, int roomId)
    {
        if (!guild.DormRooms.TryGetValue(roomId, out var state))
        {
            var theme = Theme(guild, roomId);
            guild.DormRooms.Add(roomId, state = new() { ThemeId = theme.Id, BgmIds = DefaultBgm(theme) });
        }
        return state;
    }
    private static List<int> DefaultBgm(GuildDormRoomThemeTable theme) => TableReaderV2.Parse<GuildDormBgmListTable>()
        .Single(row => row.Id == (theme.BgmListId > 0 ? theme.BgmListId : Config.CommonDefaultBgmListId)).BgmIds.ToList();

    public static GuildDormRoomData BuildRoomData(Guild guild, int roomId, int channelId)
    {
        var theme = Theme(guild, roomId);
        return new() { DormId = checked((int)guild.Id), RoomId = roomId, ChannelId = channelId, ThemeId = theme.Id,
            GuildCreateTime = guild.CreatedAt, BgmIds = guild.DormRooms.TryGetValue(roomId, out var state) && state.BgmIds.Count > 0 ? state.BgmIds.ToList() : DefaultBgm(theme) };
    }

    public static void NormalizePlayer(GuildDormPlayerState state)
    {
        long period = GuildModule.DailyPeriod(DateTimeOffset.UtcNow);
        if (state.DailyPeriod == period) return;
        state.DailyPeriod = period;
        state.DailyInteractRewardTotalTimes = RandomNumberGenerator.GetInt32(Config.DailyInteractRewardTimesMin, checked(Config.DailyInteractRewardTimesMax + 1));
        state.DailyInteractRewardCurTimes = state.DailyInteractEarnedTimes = 0;
        state.RandomBoxes.Clear();
    }

    public static void PrepareLogin(Session session)
    {
        var guild = GuildModule.FindMembership(session.player.PlayerData.Id);
        if (guild is null) return;
        var state = session.player.GuildState.Dorm;
        if (state.DailyPeriod != GuildModule.DailyPeriod(DateTimeOffset.UtcNow) || state.CurrentCharacterId == 0)
            GuildModule.Commit(session, guild, mutation =>
            {
                var staged = mutation.Player(session.player.PlayerData.Id).Dorm;
                NormalizePlayer(staged);
                if (staged.CurrentCharacterId == 0) staged.CurrentCharacterId = Config.DefaultEnterCharacterId;
            });
    }

    public static NotifyGuildDormPlayerData BuildLoginData(Session session) => Project(session.player.GuildState.Dorm);
    private static NotifyGuildDormPlayerData Project(GuildDormPlayerState state) => new()
    {
        GuildDormData = new()
        {
            CurrentCharacterId = checked((uint)(state.CurrentCharacterId == 0 ? Config.DefaultEnterCharacterId : state.CurrentCharacterId)),
            DailyInteractRewardTotalTimes = state.DailyInteractRewardTotalTimes,
            DailyInteractRewardCurTimes = state.DailyInteractRewardCurTimes,
            RandomBoxes = state.RandomBoxes.Select(Project).ToList(),
            OneTimeInteractReplyIds = state.OneTimeInteractReplyIds.Order().ToList(),
            InteractedFurnitureIds = state.InteractedFurnitureIds.Order().ToList()
        }
    };
    private static GuildDormPlayerFurnitureRandomBox Project(GuildDormRandomBoxState state) => new()
        { FurnitureId = state.FurnitureId, RandomItemId = state.RandomItemId, RandomTimes = state.RandomTimes };

    public static void OnMembershipChanged(long uid) =>
        GuildDormRoomService.ReconcileMembership(uid, GuildModule.FindMembership(uid)?.Id);

    [RequestPacketHandler("GuildDormRoomChannelDataRequest")]
    public static void Channels(Session session, Packet.Request packet)
    {
        var response = new GuildDormRoomChannelDataResponse();
        try { var guild = GuildModule.RequireMembership(session); response.ChannelDatas = GuildDormRoomService.GetChannels(guild.Id, packet.Deserialize<GuildDormRoomChannelDataRequest>().RoomId); }
        catch (ServerCodeException error) { response.Code = error.Code; }
        session.SendResponse(response, packet.Id);
    }

    [RequestPacketHandler("GuildDormPreEnterRequest")]
    public static void PreEnter(Session session, Packet.Request packet)
    {
        var response = new GuildDormPreEnterResponse();
        try
        {
            var request = packet.Deserialize<GuildDormPreEnterRequest>();
            var guild = GuildModule.RequireMembership(session);
            PrepareLogin(session);
            string token = GuildDormRoomService.IssueToken(session, request.RoomId, request.ChannelId);
            response.ConnectData = new() { IpAddress = AscNet.Common.Common.config.GameServer.Host, TcpPort = GuildDormRoomService.Port,
                Token = token, ChatChannelId = $"guild:{guild.Id}:room:{request.RoomId}:channel:{GuildDormRoomService.TokenChannelId(token)}" };
        }
        catch (ServerCodeException error) { response.Code = error.Code; }
        session.SendResponse(response, packet.Id);
    }

    [RequestPacketHandler("GuildDormSetRoomBgmIdsRequest")]
    public static void SetBgm(Session session, Packet.Request packet)
    {
        int roomId = 0;
        List<int>? changed = null;
        GuildDormSetRoomBgmIdsResponse? outcome = null;
        GuildModule.Handle<GuildDormSetRoomBgmIdsRequest, GuildDormSetRoomBgmIdsResponse>(session, packet, (mutation, request, response) =>
        {
            try { GuildModule.RequirePermission(mutation.Guild, session.player.PlayerData.Id, GuildPermission.ManageDorm); }
            catch (ServerCodeException) { throw new ServerCodeException("Insufficient dorm BGM authority", 20063338); }
            Require(request.BgmIds is { Count: > 0 }, 20173511);
            var ids = request.BgmIds.Distinct().ToList();
            var rows = TableReaderV2.Parse<GuildDormBgmTable>();
            Require(ids.All(id => rows.Any(row => row.Id == id)), 20173512);
            Require(ids.All(id => rows.Any(row => row.Id == id && (row.NeedBuy is null or 0 || mutation.Guild.DormBgms.Contains(id)
                || row.ExperienceTimeId is > 0 && IsTimeOpen(row.ExperienceTimeId)))), 20063340);
            RoomState(mutation.Guild, request.RoomId).BgmIds = ids;
            roomId = request.RoomId; changed = ids;
            outcome = response;
        }, membershipCode: 20063337);
        if (changed is not null && outcome?.Code == 0) BroadcastRoom(session, roomId, new NotifyGuildDormBgmChanged { BgmIds = changed });
    }

    [RequestPacketHandler("GuildDormSetRoomThemeRequest")]
    public static void SetTheme(Session session, Packet.Request packet)
    {
        int roomId = 0, themeId = 0;
        GuildDormSetRoomThemeResponse? outcome = null;
        GuildModule.Handle<GuildDormSetRoomThemeRequest, GuildDormSetRoomThemeResponse>(session, packet, (mutation, request, response) =>
        {
            try { GuildModule.RequirePermission(mutation.Guild, session.player.PlayerData.Id, GuildPermission.ManageDorm); }
            catch (ServerCodeException) { throw new ServerCodeException("Insufficient dorm theme authority", 20063348); }
            var theme = TableReaderV2.Parse<GuildDormRoomThemeTable>().FirstOrDefault(row => row.Id == request.ThemeId);
            Require(theme is not null, 20173021);
            Require(theme!.RoomId == request.RoomId, 20173022);
            Require(IsTimeOpen(theme.TimeId) && (theme.NeedBuy is null or 0 || mutation.Guild.DormThemes.Contains(theme.Id)), 20063349);
            var state = RoomState(mutation.Guild, request.RoomId);
            Require(state.NextSetRoomThemeTime <= Now, 20063347);
            Require(state.ThemeId != theme.Id, 20063350);
            state.ThemeId = theme.Id;
            state.BgmIds = DefaultBgm(theme);
            state.NextSetRoomThemeTime = checked(Now + Config.SetRoomThemeCd);
            response.NextSetRoomThemeTime = state.NextSetRoomThemeTime;
            roomId = request.RoomId; themeId = theme.Id;
            outcome = response;
        }, membershipCode: 20063346);
        if (themeId > 0 && outcome?.Code == 0) BroadcastRoom(session, roomId, new NotifyGuildDormThemeChanged { RoomId = roomId, ThemeId = themeId });
    }

    private static void BroadcastRoom(Session session, int roomId, object payload)
    {
        var guild = GuildModule.RequireMembership(session);
        foreach (int channel in GuildDormRoomService.Connections.Where(value => value.GuildId == guild.Id && value.RoomId == roomId).Select(value => value.ChannelId).Distinct())
            GuildDormRoomWorld.Broadcast(guild.Id, roomId, channel, payload);
    }

    [RequestPacketHandler("GuildDormGetDailyInteractRewardRequest")]
    public static void DailyReward(Session session, Packet.Request packet) =>
        GuildModule.Handle<GuildDormGetDailyInteractRewardRequest, GuildDormGetDailyInteractRewardResponse>(session, packet, (mutation, request, response) =>
        {
            var state = mutation.Player(session.player.PlayerData.Id).Dorm;
            NormalizePlayer(state);
            Require(state.DailyInteractRewardCurTimes < state.DailyInteractRewardTotalTimes, 20173513);
            GuildDormRoomWorld.AccrueReward(session, state);
            Require(state.DailyInteractEarnedTimes > state.DailyInteractRewardCurTimes, 20173513);
            response.RewardGoodsList = mutation.AddReward(session.player.PlayerData.Id, Config.DailyInteractRewardId);
            state.DailyInteractRewardCurTimes++;
            mutation.Push(session.player.PlayerData.Id, Project(state));
        });

    [RequestPacketHandler("GuildDormGetOneTimeInteractRewardRequest")]
    public static void OneTimeReward(Session session, Packet.Request packet) =>
        GuildModule.Handle<GuildDormGetOneTimeInteractRewardRequest, GuildDormGetOneTimeInteractRewardResponse>(session, packet, (mutation, request, response) =>
        {
            var row = TableReaderV2.Parse<GuildDormFurnitureInteractionTable>().FirstOrDefault(value => value.Id == request.FurnitureId);
            Require(row is not null, 20173514);
            GuildDormRoomWorld.RequireFurniture(session, request.FurnitureId);
            Require(request.ReplyIndex > 0 && request.ReplyIndex <= row!.ReplyRewardIds.Count && row.ReplyRewardIds[request.ReplyIndex - 1] > 0, 20173516);
            long id = checked((long)request.FurnitureId * 100 + request.ReplyIndex);
            var state = mutation.Player(session.player.PlayerData.Id).Dorm;
            Require(state.OneTimeInteractReplyIds.Add(id), 20173521);
            response.RewardGoodsList = mutation.AddReward(session.player.PlayerData.Id, row!.ReplyRewardIds[request.ReplyIndex - 1]);
            mutation.Push(session.player.PlayerData.Id, Project(state));
        });

    [RequestPacketHandler("GuildDormRecordInteractRequest")]
    public static void RecordInteract(Session session, Packet.Request packet) =>
        GuildModule.Handle<GuildDormRecordInteractRequest, GuildDormRecordInteractResponse>(session, packet, (mutation, request, response) =>
        {
            var row = TableReaderV2.Parse<GuildDormFurnitureInteractionTable>().FirstOrDefault(value => value.Id == request.FurnitureId);
            Require(row is not null, 20173517);
            Require(row!.NeedMark != 0, 20173519);
            GuildDormRoomWorld.RequireFurniture(session, request.FurnitureId);
            Require(mutation.Player(session.player.PlayerData.Id).Dorm.InteractedFurnitureIds.Add(request.FurnitureId), 20173520);
        });

    [RequestPacketHandler("GuildDormCallRandomBoxRequest")]
    public static void RandomBox(Session session, Packet.Request packet) =>
        GuildModule.Handle<GuildDormCallRandomBoxRequest, GuildDormCallRandomBoxResponse>(session, packet, (mutation, request, response) =>
        {
            var row = TableReaderV2.Parse<GuildDormFurnitureRandomBoxTable>().FirstOrDefault(value => value.Id == request.FurnitureId);
            Require(row is not null, 20173522);
            GuildDormRoomWorld.RequireFurniture(session, request.FurnitureId);
            var state = mutation.Player(session.player.PlayerData.Id).Dorm;
            NormalizePlayer(state);
            var box = state.RandomBoxes.FirstOrDefault(value => value.FurnitureId == request.FurnitureId);
            Require((box?.RandomTimes ?? 0) < row!.RandomTimes, 20173523);
            var items = TableReaderV2.Parse<GuildDormFurnitureRandomBoxItemTable>().Where(value => value.FurnitureId == request.FurnitureId).ToArray();
            Require(items.Length > 0, 20173524);
            if (box is null) state.RandomBoxes.Add(box = new() { FurnitureId = request.FurnitureId });
            box.RandomItemId = items[RandomNumberGenerator.GetInt32(items.Length)].Id;
            box.RandomTimes++;
            response.RandomBox = Project(box);
        });
}
