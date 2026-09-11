using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.chat;
using MongoDB.Bson.Serialization.Attributes;

namespace AscNet.Common.Database;

public sealed class OwnedChatEmoji
{
    public int Id { get; set; }
    public long EndTime { get; set; }
}

public sealed class ChatEmojiRewardOutcome
{
    public int Id { get; set; }
    public long EndTime { get; set; }
    public int ConvertItemId { get; set; }
    public int ConvertItemCount { get; set; }
}

public partial class Inventory
{
    [BsonElement("chat_emoji_reward_plans")]
    public Dictionary<string, List<ChatEmojiRewardOutcome>> ChatEmojiRewardPlans { get; set; } = [];
}

public partial class Character
{
    private static readonly Lazy<Dictionary<int, EmojiTable>> EmojiCatalog = new(() =>
        TableReaderV2.Parse<EmojiTable>().ToDictionary(row => row.Id));

    [BsonElement("chat_emojis")]
    public List<OwnedChatEmoji> ChatEmojis { get; set; } = [];

    public static EmojiTable? GetChatEmojiConfig(int id) => EmojiCatalog.Value.GetValueOrDefault(id);

    public bool CanUseChatEmoji(int id, long now)
    {
        EmojiTable? row = GetChatEmojiConfig(id);
        if (row is null) return false;
        // Preserve the existing permanent-cosmetic availability policy, not timed ownership.
        if (Convert.ToInt32(row.IsFree) == 1 || row.TimeLimitType == 1) return true;
        return ChatEmojis.Any(emoji => emoji.Id == id && emoji.EndTime > now);
    }

    public List<NotifyChatLoginData.NotifyChatLoginDataUnlockEmoji> GetUnlockedEmojis(long now) =>
        EmojiCatalog.Value.Values.Where(row => CanUseChatEmoji(row.Id, now)).Select(row =>
            new NotifyChatLoginData.NotifyChatLoginDataUnlockEmoji
            {
                Id = checked((uint)row.Id),
                EndTime = Convert.ToInt32(row.IsFree) == 1 || row.TimeLimitType == 1 ? 0
                    : checked((int)ChatEmojis.First(emoji => emoji.Id == row.Id).EndTime)
            }).ToList();
}
