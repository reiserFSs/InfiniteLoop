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

    public class PlayerMemberTargetPity
    {
        [BsonElement("since_a_or_s")]
        public int SinceAOrS { get; set; }

        [BsonElement("since_s")]
        public int SinceS { get; set; }
    }

    public class PlayerDrawState
    {
        [BsonElement("pity_rounds")]
        [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
        public Dictionary<int, PlayerDrawPityRound> PityRounds { get; set; } = new();

        [BsonElement("member_target_calibration_target_id")]
        public int MemberTargetCalibrationTargetId { get; set; }

        [BsonElement("member_target_calibration_consumed")]
        public bool MemberTargetCalibrationConsumed { get; set; }

        [BsonElement("member_target_pity")]
        public PlayerMemberTargetPity? MemberTargetPity { get; set; }

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

        // In-memory only: set when a pity round is created, cleared by
        // Player.Save/SaveChecked only after an acknowledged write matched a stored
        // document. Keeps initialization retriable after a failed or non-durable save.
        [BsonIgnore]
        public bool HasUnsavedPityRounds { get; set; }
    }

    public class PlayerDrawPityRound
    {
        public int Misses { get; set; }
        public int Limit { get; set; }
        public bool HasObtainedRare { get; set; }
        public bool GuaranteedTarget { get; set; }
        public int LowerMisses { get; set; }
    }

    public partial class Player
    {
        [BsonElement("draw_state")]
        public PlayerDrawState DrawState { get; set; } = new();
    }
}
