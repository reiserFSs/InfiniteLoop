using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.Options;
using MongoDB.Driver;

namespace AscNet.Common.Database;

public sealed partial class Guild
{
    public List<GuildInvite> Invites { get; set; } = [];
    public long KickPeriod { get; set; }
    public int KickCount { get; set; }
    public long RecruitPeriod { get; set; }
    public int RecruitCount { get; set; }
    public long ImpeachLeaderId { get; set; }
    public long ImpeachStartedAt { get; set; }
    public long ImpeachEndAt { get; set; }
    public List<long> ImpeachPetitioners { get; set; } = [];
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<long, long> TouristExpiresAt { get; set; } = [];

    public static bool HasRecruit(long uid, long now) => collection.Find(guild => guild.Active &&
        guild.Invites.Any(invite => invite.PlayerId == uid && invite.ExpiresAt > now)).Any();
}

public sealed class GuildInvite
{
    public long PlayerId { get; set; }
    public long InviterId { get; set; }
    public long CreatedAt { get; set; }
    public long ExpiresAt { get; set; }
}
