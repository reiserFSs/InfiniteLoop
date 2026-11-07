using MongoDB.Bson.Serialization.Attributes;

namespace AscNet.Common.Database;

public partial class Player
{
    /// <summary>
    /// Region of the client this player last logged in with (<see cref="Util.ClientRegions"/>); null/"en" is the shared EN tables.
    /// Set at LoginRequest from the gate request, read on reconnect, which has no gate request.
    /// </summary>
    [BsonElement("client_region")] public string? ClientRegion { get; set; }
}
