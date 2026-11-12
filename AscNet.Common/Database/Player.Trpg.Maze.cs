using MongoDB.Bson.Serialization.Attributes;

namespace AscNet.Common.Database;

/// <summary>Maze progress the client expects back at login (XTRPGMaze.UpdateData): the cards that stay finished for good
/// (TRPGMazeCard.Disposeable). Everything else about a maze visit lives in <see cref="TrpgMazeRun"/>.</summary>
[BsonIgnoreExtraElements]
public sealed class TrpgMazeState
{
    [BsonElement("finished_cards")] public List<int> FinishedCards { get; set; } = new();
}

/// <summary>One maze visit, mirrored from the client's XTRPGMaze entity (which restarts at every TRPGEnterMaze and is
/// never restored after a relogin), so it is memory-only.</summary>
public sealed class TrpgMazeRun
{
    public int MazeId { get; set; }
    public int LayerId { get; set; }
    /// <summary>1-based index into the layer's ascending node list: the node whose cards are selectable now.</summary>
    public int NodeIndex { get; set; }
    /// <summary>XTRPGMazeNode.__StartPos per (layer, node); unvisited layers stay 0 exactly like the client.</summary>
    public Dictionary<(int Layer, int Node), int> StartPos { get; } = new();
    /// <summary>Single-use cards (TRPGMazeCard.SingleDisposeable) already used this visit.</summary>
    public HashSet<int> SingleDone { get; } = new();
    /// <summary>The selected card that waits for a fight or examine result; PendingCard is the effective card id
    /// (the rolled outcome for a random card), 0 = nothing pending.</summary>
    public int PendingCard { get; set; }
    public int PendingLayer { get; set; }
    public int PendingNode { get; set; }
    public int PendingIndex { get; set; }
}

public partial class Player
{
    [BsonElement("trpg_maze")] public TrpgMazeState TrpgMaze { get; set; } = new();
    [BsonIgnore] public TrpgMazeRun? TrpgMazeRun { get; set; }
}
