using AscNet.Common;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.guildwar;
using MongoDB.Driver;

namespace AscNet.GameServer.Handlers;

internal static partial class GuildWarModule
{
    private static long SupportActor(GuildMutation mutation) => mutation.ActorSession.player.PlayerData.Id;

    private static Character? SupportCharacters(GuildMutation mutation, long uid) => uid == SupportActor(mutation)
        ? mutation.ActorSession.character : Character.collection.Find(x => x.Uid == uid).FirstOrDefault();

    internal static GuildWarFightNpcData BuildSupportNpc(GuildMutation mutation, long uid, int characterId)
    {
        Require(mutation.Guild.MemberIds.Contains(uid), 20164033);
        Character? characters = SupportCharacters(mutation, uid);
        CharacterData? character = characters?.Characters.FirstOrDefault(x => x.Id == characterId);
        Require(character is not null, 20164034);
        return new() { Character = character!, Equips = characters!.Equips.Where(x => x.CharacterId == characterId).ToList(),
            Partner = characters.Partners.FirstOrDefault(x => x.CharacterId == characterId) };
    }

    internal static void ClearSupport(GuildWarParticipation state)
    {
        state.SupportCharacterId = 0;
        foreach (GuildWarAssistTime time in state.SupportTimes.Where(x => x.EndTime == 0)) time.EndTime = Now;
        foreach (GuildWarPlayerRound round in state.Rounds) round.TeamInfo = null;
    }

    private static GuildWarSupportDetail SupportDetail(GuildMutation mutation)
    {
        GuildWarParticipation state = mutation.Player(SupportActor(mutation)).War;
        return new() { CharacterId = state.SupportCharacterId, LastRecvTime = state.SupportLastRecvTime,
            SupportSupply = state.Rounds.Where(x => x.GuildId == mutation.Guild.Id).Sum(x => x.SupportSupply),
            MyAssistRecords = state.SupportTimes.Select(x => new GuildWarAssistTime { AssistTime = Math.Max(x.AssistTime, x.ClaimedThrough), EndTime = x.EndTime }).ToList(),
            MyLogs = state.SupportLogs.Where(x => mutation.Guild.MemberIds.Contains(x.UserId)).ToList(),
            ToAssistRecords = state.Rounds.Where(x => x.GuildId == mutation.Guild.Id).Select(x => new GuildWarAssistRecord { RoundId = x.RoundId, AssistSupply = x.SupportSupply }).ToList(),
            GetAssistRecords = state.Rounds.Where(x => x.GuildId == mutation.Guild.Id).Select(x => new GuildWarAssistRecord { RoundId = x.RoundId, AssistSupply = x.ReceivedAssistSupply, TimeSupply = x.ReceivedTimeSupply }).ToList() };
    }

    [RequestPacketHandler("GuildWarOpenSupportPanelRequest")]
    public static void GuildWarOpenSupportPanelRequestHandler(Session session, Packet.Request packet)
    {
        lock (GuildModule.MembershipLock)
        {
            long uid = session.player.PlayerData.Id;
            Guild? guild = GuildModule.FindMembership(uid);
            // The first activity notification requests support details even without full guild membership.
            if (guild is null || GuildModule.Rank(guild, uid) == 5)
            {
                session.SendResponse(new GuildWarOpenSupportPanelResponse(), packet.Id);
                return;
            }
            GuildModule.Handle<GuildWarOpenSupportPanelRequest, GuildWarOpenSupportPanelResponse>(session, packet, (m, q, r) =>
            { Refresh(m); r.SupportDetail = SupportDetail(m); });
        }
    }

    [RequestPacketHandler("GuildWarSupportCharacterRequest")]
    public static void GuildWarSupportCharacterRequestHandler(Session session, Packet.Request packet) =>
        GuildModule.Handle<GuildWarSupportCharacterRequest, GuildWarSupportCharacterResponse>(session, packet, (m, q, r) =>
        {
            CurrentRound(m);
            GuildWarParticipation state = m.Player(SupportActor(m)).War;
            Require(state.SupportCharacterId == 0, 20164031);
            BuildSupportNpc(m, SupportActor(m), q.CharacterId);
            state.SupportCharacterId = q.CharacterId;
            state.SupportTimes.Add(new() { AssistTime = Now });
        });

    [RequestPacketHandler("GuildWarEndSupportRequest")]
    public static void GuildWarEndSupportRequestHandler(Session session, Packet.Request packet) =>
        GuildModule.Handle<GuildWarEndSupportRequest, GuildWarEndSupportResponse>(session, packet, (m, q, r) =>
        {
            Refresh(m);
            GuildWarParticipation state = m.Player(SupportActor(m)).War;
            Require(state.SupportCharacterId > 0, 20164032);
            Require(q.CharacterId == state.SupportCharacterId, 20164034);
            state.SupportCharacterId = 0;
            foreach (GuildWarAssistTime time in state.SupportTimes.Where(x => x.EndTime == 0)) time.EndTime = Now;
        });

    [RequestPacketHandler("GuildWarAssistCharacterListRequest")]
    public static void GuildWarAssistCharacterListRequestHandler(Session session, Packet.Request packet) =>
        GuildModule.Handle<GuildWarAssistCharacterListRequest, GuildWarAssistCharacterListResponse>(session, packet, (m, q, r) =>
        {
            CurrentRound(m);
            long actor = SupportActor(m);
            GuildWarParticipation state = m.Player(actor).War;
            foreach (long uid in m.Guild.MemberIds.Where(x => x != actor).Order())
            {
                if (r.CharacterList.Count >= Setting("AssistMaxDisplayCount")) break;
                Player? peer = Player.TryFromPlayerId(uid);
                if (peer is null || GuildModule.Rank(m.Guild, uid) == 5) continue;
                GuildWarParticipation other = m.Player(uid).War;
                if (other.Season != m.Guild.War.Season || other.SupportCharacterId <= 0
                    || SupportCharacters(m, uid)?.Characters.Any(x => x.Id == other.SupportCharacterId) != true) continue;
                r.CharacterList.Add(new() { PlayerId = uid, PlayerName = peer.PlayerData.Name,
                    LastUseTime = state.SupportUses.FirstOrDefault(x => x.PlayerId == uid && x.CharacterId == other.SupportCharacterId)?.LastUseTime ?? 0,
                    FightNpcData = BuildSupportNpc(m, uid, other.SupportCharacterId) });
            }
        });

    private static GuildWarTeamInfo CheckedTeam(GuildMutation mutation, GuildWarTeamInfo? team, bool hidden, bool allowEmpty = false)
    {
        Require(team?.CharacterInfos is { Count: 3 }, 20164035);
        Require(team!.CharacterInfos.All(x => x is not null && x.Pos is >= 1 and <= 3 && x.Id >= 0)
            && team.CharacterInfos.Select(x => x.Pos).Distinct().Count() == 3, 20164054);
        long uid = SupportActor(mutation);
        GuildWarParticipation state = mutation.Player(uid).War;
        HashSet<int> ids = [];
        int assists = 0;
        foreach (GuildWarTeamCharacterInfo member in team.CharacterInfos)
        {
            Require(member.RobotId == 0, hidden ? 20164048 : 20164030);
            if (member.Id == 0) continue;
            Require(ids.Add(member.Id), 20164050);
            Require(member.PlayerId > 0 && mutation.Guild.MemberIds.Contains(member.PlayerId), 20164033);
            BuildSupportNpc(mutation, member.PlayerId, member.Id);
            Require(!IsCharacterStationed(mutation, member.PlayerId, member.Id), 20164054);
            if (member.PlayerId == uid) continue;
            Require(++assists <= 1, 20164037);
            GuildWarParticipation peer = mutation.Player(member.PlayerId).War;
            Require(peer.Season == mutation.Guild.War.Season && peer.SupportCharacterId == member.Id
                && GuildModule.Rank(mutation.Guild, member.PlayerId) != 5, 20164034);
            long lastUse = state.SupportUses.FirstOrDefault(x => x.PlayerId == member.PlayerId && x.CharacterId == member.Id)?.LastUseTime ?? 0;
            Require(lastUse == 0 || Now - lastUse >= Setting("UseAssistCharacterCd"), 20164039);
        }
        Require((allowEmpty && ids.Count == 0) || (ids.Count > 0
            && team.CharacterInfos.Any(x => x.Pos == team.CaptainPos && x.Id > 0)
            && team.CharacterInfos.Any(x => x.Pos == team.FirstFightPos && x.Id > 0)), 20164054);
        return new() { NodeId = team.NodeId, CaptainPos = team.CaptainPos, FirstFightPos = team.FirstFightPos,
            CharacterInfos = team.CharacterInfos.Select(x => new GuildWarTeamCharacterInfo { Id = x.Id, PlayerId = x.PlayerId, RobotId = x.RobotId, Pos = x.Pos }).ToList() };
    }

    [RequestPacketHandler("GuildWarSetTeamRequest")]
    public static void GuildWarSetTeamRequestHandler(Session session, Packet.Request packet) =>
        GuildModule.Handle<GuildWarSetTeamRequest, GuildWarSetTeamResponse>(session, packet, (m, q, r) =>
        { CurrentRound(m); PlayerRound(m, SupportActor(m)).TeamInfo = CheckedTeam(m, q.TeamInfo, false); });

    internal static GuildWarTeamInfo ValidateFightTeam(GuildMutation mutation, int nodeId)
    {
        CurrentRound(mutation);
        GuildWarPlayerRound state = PlayerRound(mutation, SupportActor(mutation));
        bool hidden = TableReaderV2.Parse<GuildWarNodeTable>().Any(x => x.Id == nodeId && x.Type == 13);
        GuildWarTeamInfo? team = hidden ? state.HideAreaTeams.FirstOrDefault(x => x.NodeId == nodeId) : state.TeamInfo;
        Require(team is not null, hidden ? 20164045 : 20164035);
        Require(!hidden || team!.CurPoint == 0, 20164053);
        return CheckedTeam(mutation, team, hidden);
    }

    internal static void ConsumeTeamSupport(GuildMutation mutation, GuildWarTeamInfo team)
    {
        long uid = SupportActor(mutation);
        GuildWarParticipation state = mutation.Player(uid).War;
        foreach (GuildWarTeamCharacterInfo member in team.CharacterInfos.Where(x => x.Id > 0 && x.PlayerId != uid))
        {
            GuildWarSupportUse? use = state.SupportUses.FirstOrDefault(x => x.PlayerId == member.PlayerId && x.CharacterId == member.Id);
            if (use is null) { use = new() { PlayerId = member.PlayerId, CharacterId = member.Id }; state.SupportUses.Add(use); }
            use.LastUseTime = Now;
            if (!mutation.Guild.MemberIds.Contains(member.PlayerId) || Player.TryFromPlayerId(member.PlayerId) is null) continue;
            GuildWarParticipation peer = mutation.Player(member.PlayerId).War;
            GuildWarPlayerRound round = PlayerRound(mutation, member.PlayerId);
            int supply = Math.Min(Setting("AssistCountSupply"), Math.Max(0, Setting("AssistCountSupplyLimit") - round.ReceivedAssistSupply - round.SupportSupply));
            round.SupportSupply += supply;
            peer.SupportLogs.Insert(0, new() { UserId = uid, UserTime = Now, Supply = supply });
            int limit = Setting("AssistLogMaxCount");
            if (peer.SupportLogs.Count > limit) peer.SupportLogs.RemoveRange(limit, peer.SupportLogs.Count - limit);
        }
    }

    [RequestPacketHandler("GuildWarReceivedSupportRequest")]
    public static void GuildWarReceivedSupportRequestHandler(Session session, Packet.Request packet) =>
        GuildModule.Handle<GuildWarReceivedSupportRequest, GuildWarReceivedSupportResponse>(session, packet, (m, q, r) =>
        {
            Refresh(m);
            long uid = SupportActor(m);
            GuildWarParticipation state = m.Player(uid).War;
            foreach (GuildWarPlayerRound playerRound in state.Rounds.Where(x => x.GuildId == m.Guild.Id).OrderBy(x => x.RoundId))
            {
                GuildWarRoundState? round = m.Guild.War.Rounds.FirstOrDefault(x => x.RoundId == playerRound.RoundId);
                if (round is null) continue;
                long timeSupply = 0;
                foreach (GuildWarAssistTime time in state.SupportTimes)
                {
                    long start = Math.Max(Math.Max(time.AssistTime, time.ClaimedThrough), round.StartedAt);
                    long end = Math.Min(Math.Min(time.EndTime == 0 ? Now : time.EndTime, Now), round.EndsAt);
                    long quanta = Math.Max(0, end - start) / 600;
                    timeSupply += quanta * Setting("AssistTimeSupply");
                    if (quanta > 0) time.ClaimedThrough = start + quanta * 600;
                }
                int earned = (int)Math.Min(timeSupply, Math.Max(0, Setting("AssistTimeSupplyLimit") - playerRound.ReceivedTimeSupply));
                r.TotalSupply = checked(r.TotalSupply + earned + playerRound.SupportSupply);
                playerRound.ReceivedTimeSupply += earned;
                playerRound.ReceivedAssistSupply += playerRound.SupportSupply;
                playerRound.SupportSupply = 0;
            }
            state.SupportLastRecvTime = state.SupportTimes.Count == 0 ? Now
                : state.SupportTimes.Min(x => Math.Max(x.AssistTime, x.ClaimedThrough));
            if (r.TotalSupply > 0) m.AddGoods(uid, Setting("RewardItemId"), r.TotalSupply);
        });

    private static List<GuildWarNodeTable> HiddenNodes(GuildMutation mutation)
    {
        GuildWarRoundState round = CurrentRound(mutation);
        var nodes = TableReaderV2.Parse<GuildWarNodeTable>().Where(x => x.Type == 13 && round.Nodes.Any(n => n.NodeId == x.Id)).ToList();
        Require(nodes.Count > 0, 20164051);
        foreach (int? rootId in nodes.Select(x => x.RootId).Distinct())
        {
            Require(rootId.HasValue && rootId.Value > 0, 20164055);
            GuildWarNodeTable? root = TableReaderV2.Parse<GuildWarNodeTable>().FirstOrDefault(x => x.Id == rootId && x.Type == 11);
            Require(root is not null, 20164055);
            Require(root!.LinkIds.Where(x => x > 0).All(id => round.Nodes.Any(x => x.NodeId == id && x.IsDead > 0)), 20164043);
        }
        return nodes;
    }

    [RequestPacketHandler("GuildWarSetHideAreaTeamRequest")]
    public static void GuildWarSetHideAreaTeamRequestHandler(Session session, Packet.Request packet) =>
        GuildModule.Handle<GuildWarSetHideAreaTeamRequest, GuildWarSetHideAreaTeamResponse>(session, packet, (m, q, r) =>
        {
            List<GuildWarNodeTable> nodes = HiddenNodes(m);
            GuildWarPlayerRound state = PlayerRound(m, SupportActor(m));
            Require(q.TeamInfos is not null && q.TeamInfos.Count <= nodes.Count && q.TeamInfos.All(x => x is not null)
                && q.TeamInfos.Select(x => x.NodeId).Distinct().Count() == q.TeamInfos.Count, 20164054);
            foreach (GuildWarTeamInfo team in q.TeamInfos!)
            {
                Require(nodes.Any(x => x.Id == team.NodeId), 20164052);
                GuildWarTeamInfo? old = state.HideAreaTeams.FirstOrDefault(x => x.NodeId == team.NodeId);
                Require(old is null || old.CurPoint == 0, 20164053);
                GuildWarTeamInfo next = CheckedTeam(m, team, true, true);
                next.LastPoint = old?.LastPoint ?? 0;
                if (old is not null) state.HideAreaTeams.Remove(old);
                state.HideAreaTeams.Add(next);
            }
            var members = state.HideAreaTeams.SelectMany(x => x.CharacterInfos).Where(x => x.Id > 0).ToList();
            Require(members.Select(x => x.Id).Distinct().Count() == members.Count, 20164049);
            Require(members.Count(x => x.PlayerId != SupportActor(m)) <= 2, 20164037);
            Publish(m);
        });

    internal static void SetHiddenCandidate(GuildMutation mutation, int nodeId, long point)
    {
        Require(HiddenNodes(mutation).Any(x => x.Id == nodeId), 20164052);
        GuildWarTeamInfo team = PlayerRound(mutation, SupportActor(mutation)).HideAreaTeams.FirstOrDefault(x => x.NodeId == nodeId)
            ?? throw new ServerCodeException("Hidden team not found", 20164045);
        Require(team.CurPoint == 0, 20164053);
        Require(point > 0, 20164046);
        team.CurPoint = point;
        ConsumeTeamSupport(mutation, team);
    }

    [RequestPacketHandler("GuildWarResetHideAreaTeamRequest")]
    public static void GuildWarResetHideAreaTeamRequestHandler(Session session, Packet.Request packet) =>
        GuildModule.Handle<GuildWarResetHideAreaTeamRequest, GuildWarResetHideAreaTeamResponse>(session, packet, (m, q, r) =>
        {
            Require(HiddenNodes(m).Any(x => x.Id == q.NodeId), 20164052);
            GuildWarTeamInfo? team = PlayerRound(m, SupportActor(m)).HideAreaTeams.FirstOrDefault(x => x.NodeId == q.NodeId);
            Require(team is not null, 20164045);
            team!.CurPoint = 0;
            Publish(m);
        });

    [RequestPacketHandler("GuildWarUploadHideNodePointRequest")]
    public static void GuildWarUploadHideNodePointRequestHandler(Session session, Packet.Request packet) =>
        GuildModule.Handle<GuildWarUploadHideNodePointRequest, GuildWarUploadHideNodePointResponse>(session, packet, (m, q, r) =>
        {
            HiddenNodes(m);
            GuildWarPlayerRound state = PlayerRound(m, SupportActor(m));
            Require(state.HideAreaTeams.Count > 0, 20164044);
            long total = state.HideAreaTeams.Sum(x => x.CurPoint);
            Require(total > state.HiddenPoint, 20164046);
            long gain = total - state.HiddenPoint;
            state.Point = checked(state.Point + gain);
            CurrentRound(m).TotalPoint = checked(CurrentRound(m).TotalPoint + gain);
            state.HiddenPoint = total;
            state.LastHideAreaTeams = state.HideAreaTeams.Select(x => new GuildWarTeamInfo { NodeId = x.NodeId,
                CaptainPos = x.CaptainPos, FirstFightPos = x.FirstFightPos, CharacterInfos = x.CharacterInfos,
                LastPoint = x.CurPoint }).ToList();
            foreach (GuildWarTeamInfo team in state.HideAreaTeams) { team.LastPoint = team.CurPoint; team.CurPoint = 0; }
            Publish(m);
        });

    internal static void PopulateSupportLogin(NotifyGuildWarActivityData data, GuildWarPlayerRound? state)
    {
        data.HideAreaTeamInfos = state?.HideAreaTeams ?? [];
        data.LastHideAreaTeamInfos = state?.LastHideAreaTeams ?? [];
    }
}
