using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Commands;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.chat;
using AscNet.Table.V2.share.condition;
using AscNet.Table.V2.share.config;
using AscNet.Table.V2.share.dormitory.furniture;
using AscNet.Table.V2.share.headportrait;
using AscNet.Table.V2.share.medal;
using AscNet.Table.V2.share.nameplate;
using AscNet.Table.V2.share.scoretitle;
using MessagePack;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace AscNet.Test;

internal partial class Program
{
    // --unlock-command-only: /unlock medal|nameplate|emoji|title|chatboard|portrait <id|all> and /unlock furniture <id> [count].
    private static void ValidateUnlockCommand()
    {
        const long playerId = 99_661;
        Player player = CreateDrawCompatibilityPlayer(playerId);
        Inventory inventory = CreateDrawCompatibilityInventory(playerId, []);
        Character roster = CreateDrawCompatibilityCharacter(playerId);
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(out _, out _, out _);
        using LoopbackSessionHarness harness = new(roster, player, inventory, "unlock-command", startClientLoop: false);
        Session session = harness.Session;

        List<(string Name, byte[] Content)> Pushes(bool expectPush = false)
        {
            // pushes go through the async writer: wait for the first one when a command must send, then read until quiet
            List<(string, byte[])> pushes = [];
            for (int idle = 0, waited = 0; idle < 2;)
            {
                if (!harness.TryReadAvailablePacket("unlock pushes", out Packet packet))
                {
                    if (expectPush && pushes.Count == 0 && ++waited < 50)
                    {
                        Thread.Sleep(100);
                        continue;
                    }
                    idle++;
                    Thread.Sleep(200);
                    continue;
                }
                idle = 0;
                if (packet.Type == Packet.ContentType.Push)
                {
                    Packet.Push push = MessagePackSerializer.Deserialize<Packet.Push>(packet.Content);
                    pushes.Add((push.Name, push.Content));
                }
            }
            return pushes;
        }
        List<T> Of<T>(List<(string Name, byte[] Content)> pushes) =>
            pushes.Where(push => push.Name == typeof(T).Name).Select(push => MessagePackSerializer.Deserialize<T>(push.Content)).ToList();
        string Run(params string[] args)
        {
            try { new UnlockCommand(session, args).Execute(); }
            catch (CommandMessageCallbackException ex) { return ex.Message; }
            throw new InvalidDataException($"/unlock {string.Join(' ', args)}: expected command feedback.");
        }
        Player ReloadPlayer() => BsonSerializer.Deserialize<Player>(session.player.ToBson());
        Character ReloadCharacter() => BsonSerializer.Deserialize<Character>(session.character.ToBson());
        void Rejected(string expected, string name, params string[] args)
        {
            string before = session.player.ToBsonDocument().ToJson() + session.character.ToBsonDocument().ToJson();
            string message = Run(args);
            AssertEqual(true, message.Contains(expected, StringComparison.OrdinalIgnoreCase), $"{name}: '{message}' mentions '{expected}'");
            AssertEqual(before, session.player.ToBsonDocument().ToJson() + session.character.ToBsonDocument().ToJson(), $"{name} leaves state untouched");
            AssertEqual(0, Pushes().Count(push => push.Name != nameof(NotifyTask)), $"{name} pushes nothing");
        }
        void InvalidArgs(string name, params string[] args)
        {
            bool threw = false;
            try { _ = new UnlockCommand(session, args); }
            catch (ArgumentException) { threw = true; }
            AssertEqual(true, threw, name);
        }
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Pushes();

        // ---- argument grammar ----
        InvalidArgs("unknown kind", "banner", "1");
        InvalidArgs("non-numeric id", "medal", "abc");
        InvalidArgs("missing target", "medal");
        Rejected("only applies to furniture", "count on a non-furniture kind", "medal", "all", "2");
        Rejected("not 'all'", "furniture all", "furniture", "all");

        // ---- medal ----
        List<MedalTable> medals = TableReaderV2.Parse<MedalTable>();
        MedalTable permanentMedal = medals.First(row => Convert.ToInt64(row.KeepTime) == 0);
        MedalTable timedMedal = medals.First(row => Convert.ToInt64(row.KeepTime) > 0);
        Rejected("Unknown medal", "unknown medal", "medal", "999999");
        Run("medal", permanentMedal.Id.ToString());
        NotifyMedalData medalPush = Of<NotifyMedalData>(Pushes(true)).Single();
        AssertEqual(true, medalPush.MedalInfos.Single().Id == permanentMedal.Id && medalPush.MedalInfos.Single().KeepTime == 0, "medal push carries the unlocked medal");
        Run("medal", timedMedal.Id.ToString());
        NotifyMedalData timedPush = Of<NotifyMedalData>(Pushes(true)).Single();
        AssertEqual(Convert.ToInt64(timedMedal.KeepTime), timedPush.MedalInfos.Single().KeepTime, "timed medal keeps the table keep time");
        Rejected("1 already owned", "medal twice", "medal", permanentMedal.Id.ToString());
        string medalAll = Run("medal", "all");
        AssertEqual(true, medalAll.Contains($"Unlocked {medals.Count - 2} medals", StringComparison.Ordinal), "medal all counts the rest");
        AssertEqual(medals.Count, Of<NotifyMedalData>(Pushes(true)).Count == 1 ? ReloadPlayer().UnlockedMedals.Count : -1, "medal all persists every medal once");
        AssertEqual(true, ReloadPlayer().UnlockedMedals.Single(medal => medal.Id == timedMedal.Id).KeepTime == Convert.ToInt64(timedMedal.KeepTime), "timed medal survives reload");

        // ---- nameplate ----
        List<NameplateTable> plates = TableReaderV2.Parse<NameplateTable>();
        List<NameplateTable> grantable = plates.Where(row => Character.IsGrantableNameplate(row)).ToList();
        NameplateTable? ungrantable = plates.FirstOrDefault(row => !Character.IsGrantableNameplate(row));
        List<IGrouping<int, NameplateTable>> permanentGroups = grantable.Where(row => row.Duration == 0).GroupBy(row => row.Group)
            .Where(group => group.Count() >= 2).Take(2).ToList();
        NameplateTable plateLow = permanentGroups[0].OrderBy(row => row.NameplateQuality).First();
        NameplateTable plateHigh = permanentGroups[0].OrderBy(row => row.NameplateQuality).Last();
        NameplateTable plateOther = permanentGroups[1].OrderBy(row => row.NameplateQuality).First();
        Rejected("Unknown nameplate", "unknown nameplate", "nameplate", "999999");
        if (ungrantable is not null)
            Rejected("not a directly awardable", "ungrantable nameplate", "nameplate", ungrantable.Id.ToString());
        Run("nameplate", plateLow.Id.ToString());
        AssertEqual(plateLow.Id, Of<NotifyNameplateInfo>(Pushes(true)).Single().Nameplate.Id, "nameplate push");
        Run("nameplate", plateOther.Id.ToString());
        AssertEqual(plateOther.Id, Of<NotifyNameplateInfo>(Pushes(true)).Single().Nameplate.Id, "second nameplate push");
        Rejected("1 already owned", "nameplate twice", "nameplate", plateLow.Id.ToString());
        Run("nameplate", plateHigh.Id.ToString());
        AssertEqual(plateHigh.Id, Of<NotifyNameplateInfo>(Pushes(true)).Single().Nameplate.Id, "higher tier replaces the lower one");
        Rejected("1 already owned", "lower tier after higher", "nameplate", plateLow.Id.ToString());
        Run("nameplate", "all");
        List<NotifyNameplateInfo> plateAllPush = Of<NotifyNameplateInfo>(Pushes(true));
        int expectedGroups = grantable.Select(row => row.Group).Distinct().Count();
        AssertEqual(expectedGroups, session.character.Nameplates.Count, "nameplate all owns one plate per grantable group");
        AssertEqual(expectedGroups - 1, plateAllPush.Count, "nameplate all pushes only the changed groups");
        AssertEqual(true, session.character.Nameplates.All(owned =>
            Character.GetNameplateConfig(owned.Id)!.NameplateQuality == grantable.Where(row => row.Group == Character.GetNameplateConfig(owned.Id)!.Group).Max(row => row.NameplateQuality)),
            "nameplate all picks each group's top tier");
        Rejected("0 nameplates", "nameplate all again", "nameplate", "all");
        AssertEqual(expectedGroups, ReloadCharacter().Nameplates.Count, "nameplates survive reload");

        // ---- emoji ----
        List<EmojiTable> emojis = TableReaderV2.Parse<EmojiTable>();
        List<EmojiTable> timedEmojis = emojis.Where(row => row.TimeLimitType == 2).Take(2).ToList();
        EmojiTable permanentEmoji = emojis.First(row => row.TimeLimitType == 1);
        Rejected("Unknown emoji", "unknown emoji", "emoji", "999999");
        Rejected("1 already owned", "permanent emoji is already usable", "emoji", permanentEmoji.Id.ToString());
        Run("emoji", timedEmojis[0].Id.ToString());
        NotifyChatEmoji emojiPush = Of<NotifyChatEmoji>(Pushes(true)).Single();
        AssertEqual((uint)timedEmojis[0].Id, emojiPush.Emoji.Id, "emoji push id");
        AssertEqual(true, emojiPush.Emoji.EndTime >= now + Convert.ToInt64(timedEmojis[0].Duration) - 5, "timed emoji ends after its table duration");
        Rejected("1 already owned", "timed emoji twice", "emoji", timedEmojis[0].Id.ToString());
        Run("emoji", timedEmojis[1].Id.ToString());
        AssertEqual((uint)timedEmojis[1].Id, Of<NotifyChatEmoji>(Pushes(true)).Single().Emoji.Id, "second emoji push id");
        Run("emoji", "all");
        Pushes();
        int timedCount = emojis.Count(row => row.TimeLimitType == 2);
        AssertEqual(timedCount, session.character.ChatEmojis.Count(emoji => emoji.EndTime > now), "emoji all owns every timed emoji");
        AssertEqual(timedCount, ReloadCharacter().ChatEmojis.Count(emoji => emoji.EndTime > now), "emojis survive reload");
        AssertEqual(0, session.inventory.Items.Count, "emoji unlocks never convert into items");

        // ---- title ----
        List<ScoreTitleTable> titles = TableReaderV2.Parse<ScoreTitleTable>();
        Dictionary<int, ConditionTable> conditions = TableReaderV2.Parse<ConditionTable>().Where(row => row.Type == 15103).ToDictionary(row => row.Id);
        bool Awardable(ScoreTitleTable row) => row.QualityConditions.Count == row.Qualities.Count
            && row.QualityConditions.All(id => conditions.TryGetValue(id, out ConditionTable? c) && c.Params.Count >= 2 && c.Params[0] == row.Id);
        ScoreTitleTable plainTitle = titles.First(row => Awardable(row) && row.QualityConditions.Count == 0);
        ScoreTitleTable steppedTitle = titles.First(row => Awardable(row) && row.QualityConditions.Count > 0);
        ScoreTitleTable? brokenTitle = titles.FirstOrDefault(row => !Awardable(row));
        Rejected("Unknown title", "unknown title", "title", "999999");
        if (brokenTitle is not null)
            Rejected("cannot award", "unawardable title", "title", brokenTitle.Id.ToString());
        Run("title", plainTitle.Id.ToString());
        NotifyScoreTitleInfo plainPush = Of<NotifyScoreTitleInfo>(Pushes(true)).Single();
        AssertEqual(true, plainPush.IsLogined && plainPush.Titles.Single().Id == (uint)plainTitle.Id && plainPush.Titles.Single().Quality == plainTitle.InitQuality, "plain title push");
        Run("title", steppedTitle.Id.ToString());
        NotifyScoreTitleInfo steppedPush = Of<NotifyScoreTitleInfo>(Pushes(true)).Single();
        AssertEqual(steppedTitle.QualityConditions.Max(id => conditions[id].Params[1]), steppedPush.Titles.Single().Score, "stepped title reaches the top threshold");
        AssertEqual(Math.Max(steppedTitle.InitQuality, steppedTitle.Qualities.Max()), steppedPush.Titles.Single().Quality, "stepped title reaches the top quality");
        Rejected("1 already owned", "title twice", "title", steppedTitle.Id.ToString());
        Run("title", "all");
        Pushes();
        AssertEqual(titles.Count(Awardable), session.character.ScoreTitles.Count, "title all owns every awardable title");
        AssertEqual(titles.Count(Awardable), ReloadCharacter().ScoreTitles.Count, "titles survive reload");
        AssertEqual(steppedPush.Titles.Single().Score, ReloadCharacter().ScoreTitles.Single(title => title.Id == (uint)steppedTitle.Id).Score, "title all does not stack score");

        // ---- chat board ----
        List<ChatBoardTable> boards = TableReaderV2.Parse<ChatBoardTable>();
        ChatBoardTable permanentBoard = boards.First(row => row.Duration == 0);
        ChatBoardTable timedBoard = boards.First(row => row.Duration > 0);
        Rejected("Unknown chat board", "unknown chat board", "chatboard", "999999");
        Run("chatboard", permanentBoard.Id.ToString());
        NotifyChatBoardInfo boardPush = Of<NotifyChatBoardInfo>(Pushes(true)).Single();
        AssertEqual(true, boardPush.ChatBoard.Id == permanentBoard.Id && boardPush.ChatBoard.EndTime == 0, "permanent chat board push");
        Run("chatboard", timedBoard.Id.ToString());
        NotifyChatBoardInfo timedBoardPush = Of<NotifyChatBoardInfo>(Pushes(true)).Single();
        AssertEqual(true, timedBoardPush.ChatBoard.EndTime >= now + timedBoard.Duration - 5, "timed chat board ends after its duration");
        Rejected("1 already owned", "chat board twice", "chatboard", permanentBoard.Id.ToString());
        Run("chatboard", "all");
        Pushes();
        AssertEqual(boards.Count, ReloadPlayer().UnlockedChatBoards.Select(unlock => unlock.Id).Distinct().Count(), "chatboard all owns every board once");

        // ---- portrait ----
        List<HeadPortraitTable> heads = TableReaderV2.Parse<HeadPortraitTable>();
        List<HeadPortraitTable> visible = heads.Where(row => Convert.ToInt32(row.Expire) == 0).ToList();
        HeadPortraitTable? retired = heads.FirstOrDefault(row => Convert.ToInt32(row.Expire) != 0);
        HeadPortraitTable[] free = visible.Where(row => session.player.HeadPortraits.All(head => head.Id != row.Id)).Take(2).ToArray();
        Rejected("Unknown portrait", "unknown portrait", "portrait", "999999");
        if (retired is not null)
            Rejected("retired", "retired portrait", "portrait", retired.Id.ToString());
        Run("portrait", free[0].Id.ToString());
        AssertEqual((long)free[0].Id, Of<NotifyHeadPortraitInfos>(Pushes(true)).Single().Heads.Single().Id, "portrait push");
        Run("portrait", free[1].Id.ToString());
        AssertEqual((long)free[1].Id, Of<NotifyHeadPortraitInfos>(Pushes(true)).Single().Heads.Single().Id, "second portrait push");
        Rejected("1 already owned", "portrait twice", "portrait", free[0].Id.ToString());
        Run("portrait", "all");
        Pushes();
        AssertEqual(visible.Count, session.player.HeadPortraits.Select(head => head.Id).Distinct().Count(row => visible.Any(v => v.Id == row)), "portrait all owns every visible portrait");
        AssertEqual(true, retired is null || ReloadPlayer().HeadPortraits.All(head => head.Id != retired.Id), "portrait all skips retired entries");

        // ---- furniture ----
        HashSet<int> grantableFurniture = TableReaderV2.Parse<FurnitureRewardTable>().Select(row => row.FurnitureId).ToHashSet();
        List<FurnitureTable> furniture = TableReaderV2.Parse<FurnitureTable>();
        FurnitureTable furnitureA = furniture.First(row => grantableFurniture.Contains(row.Id));
        FurnitureTable furnitureB = furniture.First(row => grantableFurniture.Contains(row.Id) && row.Id != furnitureA.Id);
        FurnitureTable? noReward = furniture.FirstOrDefault(row => !grantableFurniture.Contains(row.Id));
        int cap = int.Parse(TableReaderV2.Parse<ConfigTable>().Single(row => row.Key == "DormMaxTotalFurnitureCount").Value);
        Rejected("Unknown furniture", "unknown furniture", "furniture", "999999");
        if (noReward is not null)
            Rejected("cannot be granted", "furniture without reward row", "furniture", noReward.Id.ToString());
        int baseline;
        Rejected("positive", "zero furniture count", "furniture", furnitureA.Id.ToString(), "0");
        Run("furniture", furnitureA.Id.ToString(), "3");
        NotifyDormitoryData dormPush = Of<NotifyDormitoryData>(Pushes(true)).Single();
        AssertEqual(3, dormPush.FurnitureList.Count(entry => entry.ConfigId == (uint)furnitureA.Id), "furniture push lists the added copies");
        AssertEqual(3, session.player.Dorm.Furniture.Count(entry => entry.ConfigId == (uint)furnitureA.Id), "three copies stored");
        AssertEqual(session.player.Dorm.Furniture.Count, session.player.Dorm.Furniture.Select(entry => entry.Id).Distinct().Count(), "furniture copies get distinct ids");
        baseline = session.player.Dorm.Furniture.Count - 3; // the login payload also materialises the free rooms' default furniture
        Run("furniture", furnitureB.Id.ToString());
        AssertEqual(1, Of<NotifyDormitoryData>(Pushes(true)).Single().FurnitureList.Count(entry => entry.ConfigId == (uint)furnitureB.Id), "default count is one");
        Player reloaded = ReloadPlayer();
        AssertEqual(baseline + 4, reloaded.Dorm.Furniture.Count, "furniture survives reload");
        AssertEqual(true, reloaded.Dorm.FurnitureUnlocks.Contains((uint)furnitureA.Id), "furniture blueprint unlocked");
        int free2 = cap - session.player.Dorm.Furniture.Count;
        Rejected("only", "furniture over the cap", "furniture", furnitureA.Id.ToString(), (free2 + 1).ToString());
        AssertEqual(baseline + 4, session.player.Dorm.Furniture.Count, "over-cap request stores nothing");
        string last = Run("furniture", furnitureA.Id.ToString(), free2.ToString());
        AssertEqual(true, last.Contains($"{cap} of {cap}", StringComparison.Ordinal), "filling to the cap reports it");
        AssertEqual(cap, session.player.Dorm.Furniture.Count, "cap reached exactly");
        Pushes();
        Rejected("only 0 more", "full storage", "furniture", furnitureA.Id.ToString());
    }
}
