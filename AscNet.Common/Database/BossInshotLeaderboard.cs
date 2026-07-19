using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace AscNet.Common.Database;

public sealed class BossInshotRankEntry
{
    public static readonly IMongoCollection<BossInshotRankEntry> collection =
        Common.db.GetCollection<BossInshotRankEntry>("boss_inshot_rank_entries");

    [BsonId]
    public string Id { get; set; } = string.Empty;

    [BsonElement("activity_id")]
    public int ActivityId { get; set; }

    [BsonElement("boss_id")]
    public int BossId { get; set; }

    [BsonElement("character_id")]
    public int CharacterId { get; set; }

    [BsonElement("player_id")]
    public long PlayerId { get; set; }

    [BsonElement("name")]
    public string Name { get; set; } = string.Empty;

    [BsonElement("head_portrait_id")]
    public long HeadPortraitId { get; set; }

    [BsonElement("head_frame_id")]
    public long HeadFrameId { get; set; }

    [BsonElement("tower_id")]
    public int TowerId { get; set; }

    [BsonElement("score")]
    public int Score { get; set; }

    [BsonElement("achieved_at")]
    public long AchievedAt { get; set; }

    public static string BuildId(int activityId, int bossId, int characterId, long playerId) =>
        $"{activityId}:{bossId}:{characterId}:{playerId}";

    public static void EnsureIndexes()
    {
        collection.Indexes.CreateOne(new CreateIndexModel<BossInshotRankEntry>(
            Builders<BossInshotRankEntry>.IndexKeys
                .Ascending(entry => entry.ActivityId)
                .Ascending(entry => entry.BossId)
                .Ascending(entry => entry.CharacterId)
                .Descending(entry => entry.TowerId)
                .Descending(entry => entry.Score)
                .Ascending(entry => entry.AchievedAt)
                .Ascending(entry => entry.PlayerId),
            new CreateIndexOptions
            {
                Name = "activity_boss_character_rank"
            }));
    }
}
