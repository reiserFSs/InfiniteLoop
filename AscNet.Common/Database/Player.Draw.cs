using AscNet.Common.MsgPack;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.Options;

namespace AscNet.Common.Database
{
    public class PlayerDrawProgress
    {
        [BsonElement("today_count")]
        public int TodayCount { get; set; }

        [BsonElement("total_count")]
        public int TotalCount { get; set; }
    }

    public class PlayerDrawHistoryRecord
    {
        [BsonElement("reward_goods")]
        public RewardGoods RewardGoods { get; set; } = new();

        [BsonElement("draw_time")]
        public long DrawTime { get; set; }
    }

    public class PlayerDrawSelectionState
    {
        [BsonElement("slots")]
        [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
        public Dictionary<int, int> Slots { get; set; } = new();
    }

    public class PlayerDrawHistoryGroupState
    {
        [BsonElement("history_by_sub_type")]
        [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
        public Dictionary<int, List<PlayerDrawHistoryRecord>> HistoryBySubType { get; set; } = new();
    }

    public class PlayerDrawState
    {
        [BsonElement("progress_by_draw_id")]
        [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
        public Dictionary<int, PlayerDrawProgress> ProgressByDrawId { get; set; } = new();

        [BsonElement("pity_count_by_group")]
        [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
        public Dictionary<int, int> PityCountByGroup { get; set; } = new();

        [BsonElement("selected_draw_by_group")]
        [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
        public Dictionary<int, PlayerDrawSelectionState> SelectedDrawByGroup { get; set; } = new();

        [BsonElement("switch_count_by_group")]
        [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
        public Dictionary<int, int> SwitchCountByGroup { get; set; } = new();

        [BsonElement("history_by_group")]
        [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
        public Dictionary<int, PlayerDrawHistoryGroupState> HistoryByGroup { get; set; } = new();
    }

    public partial class Player
    {
        [BsonElement("draw_state")]
        public PlayerDrawState DrawState { get; set; } = new();
    }
}
