using AscNet.Common;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.guild;
using AscNet.Table.V2.client.config;
using MessagePack;
using MongoDB.Driver;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AscNet.GameServer.Handlers;

[MessagePackObject(true)]
public class GuildChangeIconRequest { public int IconId; }
[MessagePackObject(true)]
public class GuildChangeIconResponse : GuildResponse { }
[MessagePackObject(true)]
public class GuildChangeNameRequest { public string Name = string.Empty; }
[MessagePackObject(true)]
public class GuildChangeNameResponse : GuildResponse { public string Name = string.Empty; }
[MessagePackObject(true)]
public class GuildChangeDeclarationRequest { public string Delaration = string.Empty; }
[MessagePackObject(true)]
public class GuildChangeDeclarationResponse : GuildResponse { public string Delaration = string.Empty; }
[MessagePackObject(true)]
public class GuildChangeNoticeRequest { public string Notice = string.Empty; }
[MessagePackObject(true)]
public class GuildChangeNoticeResponse : GuildResponse { public string Notice = string.Empty; }
[MessagePackObject(true)]
public class GuildChangeApplyOptionRequest { public int Option; public int MinLevel; }
[MessagePackObject(true)]
public class GuildChangeApplyOptionResponse : GuildResponse { }
[MessagePackObject(true)]
public class GuildChangeScriptRequest { public List<string> Scripts = []; }
[MessagePackObject(true)]
public class GuildChangeScriptResponse : GuildResponse { public List<string> Scripts = []; }
[MessagePackObject(true)]
public class GuildChangeRankNameRequest { public string AllRankName = string.Empty; }
[MessagePackObject(true)]
public class GuildChangeRankNameResponse : GuildResponse { }
[MessagePackObject(true)]
public class NotifyRankName { public string AllRankName = string.Empty; }

internal partial class GuildModule
{
    private sealed class IdentityRankName
    {
        public IdentityRankName() { }
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    // Count Unicode scalar values, matching the client's UTF-8 length rather than UTF-16 code units.
    private static int IdentityLength(string text) => text.EnumerateRunes().Count();
    private static bool IdentityTextValid(string? text)
    {
        if (text is null) return false;
        for (int index = 0; index < text.Length; index++)
        {
            if (char.IsHighSurrogate(text[index]))
            {
                if (++index >= text.Length || !char.IsLowSurrogate(text[index])) return false;
            }
            else if (char.IsLowSurrogate(text[index])) return false;
        }
        // Reject control/formatting and rich-text delimiters; no invented profanity service.
        return !text.EnumerateRunes().Any(rune => Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control
            or UnicodeCategory.Format or UnicodeCategory.OtherNotAssigned || rune.Value is '<' or '>');
    }

    private static void IdentityAdmin(GuildMutation tx, int code) =>
        Require(Rank(tx.Guild, tx.ActorSession.player.PlayerData.Id) is 1 or 2, code);

    private static void PushIdentitySettings(GuildMutation tx)
    {
        // NotifyGuildData causes the EN consumer to refetch detail, including notice/apply settings.
        foreach (long uid in tx.Guild.MemberIds)
            tx.Push(uid, BuildLoginData(tx.Guild, uid, tx.Player(uid)));
    }

    [RequestPacketHandler("GuildChangeIconRequest")]
    public static void GuildChangeIconRequestHandler(Session session, Packet.Request packet) =>
        Handle<GuildChangeIconRequest, GuildChangeIconResponse>(session, packet, (tx, request, response) =>
        {
            IdentityAdmin(tx, 20063048);
            Require(OwnsGuildPortrait(tx.Guild, request.IconId), 20063049);
            tx.Guild.IconId = request.IconId;
            PushIdentitySettings(tx);
        }, membershipCode: 20063045, fallbackCode: 20063050);

    [RequestPacketHandler("GuildChangeNameRequest")]
    public static void GuildChangeNameRequestHandler(Session session, Packet.Request packet) =>
        Handle<GuildChangeNameRequest, GuildChangeNameResponse>(session, packet, (tx, request, response) =>
        {
            response.Name = request.Name ?? string.Empty;
            long uid = session.player.PlayerData.Id;
            Require(Rank(tx.Guild, uid) == 1, 20063246);
            Require(!string.IsNullOrWhiteSpace(request.Name), 20063239);
            Require(IdentityLength(request.Name) >= Setting("GuildNameMinLen")
                && IdentityLength(request.Name) <= Setting("GuildNameMaxLen"), 20063240);
            Require(IdentityTextValid(request.Name), 20063241);
            Require(tx.Guild.Name != request.Name, 20063242);
            Require(!Guild.collection.Find(guild => guild.Id != tx.Guild.Id && (guild.Active || guild.CreationReserved)
                && guild.Name == request.Name).Any(), 20063244);
            int previousFreeCount = tx.Guild.FreeChangeGuildNameCount;
            if (tx.Guild.FreeChangeGuildNameCount > 0)
                tx.Guild.FreeChangeGuildNameCount--;
            else
            {
                // EN Item 53 is the Command Bureau Name Change Card, consumed once per rename.
                Require(tx.Balance(uid, 53) >= 1, 20063243);
                tx.AddCost(uid, 53, 1);
            }
            tx.Guild.Name = request.Name;
            foreach (long memberId in tx.Guild.MemberIds)
            {
                var identity = BuildLoginData(tx.Guild, memberId, tx.Player(memberId));
                // The requesting client's success callback consumes one free opportunity locally.
                if (memberId == uid) identity.FreeChangeGuildNameCount = previousFreeCount;
                tx.Push(memberId, identity);
            }
        }, membershipCode: 20063238, fallbackCode: 20063245, duplicateKeyCode: 20063244);

    [RequestPacketHandler("GuildChangeDeclarationRequest")]
    public static void GuildChangeDeclarationRequestHandler(Session session, Packet.Request packet) =>
        Handle<GuildChangeDeclarationRequest, GuildChangeDeclarationResponse>(session, packet, (tx, request, response) =>
        {
            response.Delaration = request.Delaration ?? string.Empty;
            IdentityAdmin(tx, 20063056);
            Require(request.Delaration is not null && IdentityLength(request.Delaration) <= Setting("GuildDeclarationMaxLen"), 20063052);
            Require(IdentityTextValid(request.Delaration), 20063053);
            tx.Guild.Declaration = request.Delaration;
            PushIdentitySettings(tx);
        }, membershipCode: 20063051, fallbackCode: 20063057);

    [RequestPacketHandler("GuildChangeNoticeRequest")]
    public static void GuildChangeNoticeRequestHandler(Session session, Packet.Request packet) =>
        Handle<GuildChangeNoticeRequest, GuildChangeNoticeResponse>(session, packet, (tx, request, response) =>
        {
            response.Notice = request.Notice ?? string.Empty;
            // EN has no separate Notice CodeText family; internal comms shares declaration validation.
            IdentityAdmin(tx, 20063056);
            int limit = Convert.ToInt32(TableReaderV2.Parse<ClientConfigTable>().Single(row => row.Key == "GuildInterComMaxLen").Value, CultureInfo.InvariantCulture);
            Require(request.Notice is not null && IdentityLength(request.Notice) <= limit, 20063052);
            Require(IdentityTextValid(request.Notice), 20063053);
            tx.Guild.Notice = request.Notice;
            PushIdentitySettings(tx);
        }, membershipCode: 20063051, fallbackCode: 20063057);

    [RequestPacketHandler("GuildChangeApplyOptionRequest")]
    public static void GuildChangeApplyOptionRequestHandler(Session session, Packet.Request packet) =>
        Handle<GuildChangeApplyOptionRequest, GuildChangeApplyOptionResponse>(session, packet, (tx, request, response) =>
        {
            IdentityAdmin(tx, 20063220);
            Require(request.Option is DirectAdmission or NeedsApproval or ForbiddenAdmission
                && request.MinLevel >= Setting("PlayerMinLevel") && request.MinLevel <= Setting("PlayerMaxLevel"), 20063217);
            tx.Guild.Option = request.Option;
            tx.Guild.MinLevel = request.MinLevel;
            PushIdentitySettings(tx);
        }, membershipCode: 20063216, fallbackCode: 20063217);

    [RequestPacketHandler("GuildChangeScriptRequest")]
    public static void GuildChangeScriptRequestHandler(Session session, Packet.Request packet) =>
        Handle<GuildChangeScriptRequest, GuildChangeScriptResponse>(session, packet, (tx, request, response) =>
        {
            Require(request.Scripts is not null, 20063126);
            Require(request.Scripts.Count <= Setting("GuildScriptCount"), 20063129);
            foreach (string script in request.Scripts)
            {
                Require(script is not null && IdentityLength(script) <= Setting("GuildScriptLength"), 20063128);
                Require(IdentityTextValid(script), 20063127);
            }
            tx.Player(session.player.PlayerData.Id).Scripts = [.. request.Scripts];
            response.Scripts = [.. request.Scripts];
        }, membershipCode: 20063125, fallbackCode: 20063133);

    [RequestPacketHandler("GuildChangeRankNameRequest")]
    public static void GuildChangeRankNameRequestHandler(Session session, Packet.Request packet) =>
        Handle<GuildChangeRankNameRequest, GuildChangeRankNameResponse>(session, packet, (tx, request, response) =>
        {
            IdentityAdmin(tx, 20063063);
            List<IdentityRankName>? requested;
            try { requested = JsonSerializer.Deserialize<List<IdentityRankName>>(request.AllRankName ?? string.Empty); }
            catch (JsonException) { throw new ServerCodeException("Invalid guild rank names", 20063060); }
            GuildPositionTable[] positions = TableReaderV2.Parse<GuildPositionTable>().ToArray();
            Require(requested is not null && requested.Count == positions.Length
                && requested.Select(row => row?.Id).Distinct().Count() == positions.Length, 20063060);
            Dictionary<int, string> previous = string.IsNullOrEmpty(tx.Guild.RankNames)
                ? positions.ToDictionary(row => row.Id, row => row.Name)
                : JsonSerializer.Deserialize<List<IdentityRankName>>(tx.Guild.RankNames)!.ToDictionary(row => row.Id, row => row.Name);
            GuildCustomNameTable[] choices = TableReaderV2.Parse<GuildCustomNameTable>().ToArray();
            int actorRank = Rank(tx.Guild, session.player.PlayerData.Id);
            foreach (IdentityRankName row in requested!)
            {
                Require(row is not null && previous.ContainsKey(row.Id) && IdentityTextValid(row.Name), 20063060);
                Require(actorRank <= row.Id || row.Name == previous[row.Id], 20063063);
                Require(row.Name == previous[row.Id] || choices.Any(choice => choice.Enable == 1
                    && choice.RankLevel == row.Id && choice.Name == row.Name), 20063060);
            }
            Require(requested.Select(row => row.Name).Distinct(StringComparer.Ordinal).Count() == positions.Length, 20063060);
            tx.Guild.RankNames = JsonSerializer.Serialize(requested.OrderBy(row => row.Id));
            tx.Broadcast(new NotifyRankName { AllRankName = tx.Guild.RankNames });
        }, membershipCode: 20063059, fallbackCode: 20063064);
}
