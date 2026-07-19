using MongoDB.Bson.Serialization.Attributes;

namespace AscNet.Common.Database
{
    public sealed class PlayerMailRewardGoods
    {
        [BsonElement("reward_type")] public int RewardType { get; set; }
        [BsonElement("template_id")] public uint TemplateId { get; set; }
        [BsonElement("count")] public int Count { get; set; }
        [BsonElement("level")] public int Level { get; set; }
        [BsonElement("quality")] public int Quality { get; set; }
        [BsonElement("grade")] public int Grade { get; set; }
        [BsonElement("breakthrough")] public int Breakthrough { get; set; }
        [BsonElement("convert_from")] public int ConvertFrom { get; set; }
        [BsonElement("is_gift")] public bool IsGift { get; set; }
        [BsonElement("reward_multi")] public int RewardMulti { get; set; }
        [BsonElement("id")] public int Id { get; set; }
    }

    public sealed class PlayerMail
    {
        [BsonElement("id")] public string Id { get; set; } = string.Empty;
        [BsonElement("group_id")] public int GroupId { get; set; }
        [BsonElement("batch_id")] public string? BatchId { get; set; }
        [BsonElement("type")] public int Type { get; set; }
        [BsonElement("status")] public int Status { get; set; }
        [BsonElement("send_name")] public string SendName { get; set; } = string.Empty;
        [BsonElement("title")] public string Title { get; set; } = string.Empty;
        [BsonElement("content")] public string Content { get; set; } = string.Empty;
        [BsonElement("create_time")] public long CreateTime { get; set; }
        [BsonElement("send_time")] public long SendTime { get; set; }
        [BsonElement("expire_time")] public long ExpireTime { get; set; }
        [BsonElement("reward_goods_list")] public List<PlayerMailRewardGoods>? RewardGoodsList { get; set; }
        [BsonElement("is_forbid_delete")] public bool IsForbidDelete { get; set; }
        [BsonElement("is_survey")] public bool IsSurvey { get; set; }
        [BsonElement("reserve_time")] public long ReserveTime { get; set; }
    }

    public partial class Player
    {
        [BsonElement("mails")] public List<PlayerMail> Mails { get; set; } = new();
        [BsonElement("mail_expire_ids")] public List<string> MailExpireIds { get; set; } = new();
    }
}
