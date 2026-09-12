using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Table.V2.share.reward;
using AscNet.Table.V2.share.theatre5;
using AscNet.Table.V2.share.theatre5.theatre5pve;

namespace AscNet.GameServer.Handlers;

internal static partial class Theatre5Module
{
    public static bool IsStoryContentComplete(Player player, int contentId) =>
        contentId > 0 && player.Theatre5.Data.PveStoryLines.Values.Any(story => story.FinishContents.Contains(contentId));

    internal static void InitializePveState(Mutation mutation)
    {
        Dictionary<int, Theatre5StoryLine> unlocked = [];
        Dictionary<int, Theatre5PveCharacter> characters = [];
        foreach (var row in Rows<Theatre5PveStoryLineTable>().OrderBy(row => row.Id))
        {
            if (!IsConditionMet(mutation.Session, row.StoryLineCondition ?? 0, mutation.State)) continue;
            if (!mutation.Data.PveStoryLines.ContainsKey(row.Id))
            {
                int first = Rows<Theatre5PveStoryLineContentTable>().Where(content => content.StoryLineId == row.Id).Min(content => content.Id);
                var story = new Theatre5StoryLine { StoryLineId = row.Id, CurContentId = first };
                mutation.Data.PveStoryLines.Add(row.Id, story);
                unlocked.Add(row.Id, story);
            }
            // Local initialization policy: unlocked authored storylines expose only
            // their authored playable characters, with the first authored fashion.
            foreach (int id in row.StoryLineCharacter.Where(id => id > 0))
            {
                if (mutation.Data.PveCharacters.ContainsKey(id)) continue;
                var config = Rows<Theatre5CharacterTable>().Single(character => character.Id == id && character.Priority > 0);
                var character = new Theatre5PveCharacter { FashionId = config.FashionIds[0] };
                mutation.Data.PveCharacters.Add(id, character);
                characters.Add(id, character);
            }
        }
        UnlockPveKnowledge(mutation);
        if (unlocked.Count > 0) mutation.Push(new NotifyPveStoryLineUnlock { PveStoryLines = unlocked });
        if (characters.Count > 0) mutation.Push(new NotifyTheatre5UnlockCharacter { PveCharacters = characters });
    }

    private static Theatre5StoryLine PveStory(Mutation mutation, int id)
    {
        Require(mutation.Data.PveStoryLines.TryGetValue(id, out var story), 20282017);
        return story;
    }

    private static Theatre5PveStoryLineContentTable PveContent(int id)
    {
        var row = Rows<Theatre5PveStoryLineContentTable>().FirstOrDefault(row => row.Id == id);
        Require(row != null, 20282003);
        return row;
    }

    private static bool PveStoryComplete(Theatre5StoryLine story) =>
        Rows<Theatre5PveStoryLineContentTable>().Where(row => row.StoryLineId == story.StoryLineId)
            .All(row => story.FinishContents.Contains(row.Id));

    private static void CompletePveContent(Mutation mutation, Theatre5StoryLine story, int? selection = null)
    {
        var current = PveContent(story.CurContentId);
        int next;
        if (current.ContentType == 7)
        {
            int index = current.ChooseContents.IndexOf(selection ?? 0);
            Require(index >= 0, 20284001);
            Require(index < current.ChooseConditions.Count && IsConditionMet(mutation.Session, current.ChooseConditions[index], mutation.State), 20284001);
            next = current.ChooseContents[index];
        }
        else if (current.ContentType == 8)
        {
            // Local backtrack policy: nearest prior branch in this same storyline.
            // Preserve completed history so both authored endings remain discoverable.
            var branch = Rows<Theatre5PveStoryLineContentTable>().Where(row => row.StoryLineId == story.StoryLineId
                && row.ContentType == 7 && row.Id < current.Id).MaxBy(row => row.Id);
            Require(branch != null, 20282003);
            next = branch.Id;
        }
        else
        {
            Require(selection == null || selection == 0, 20284001);
            next = Rows<Theatre5PveStoryLineContentTable>().Where(row => row.StoryLineId == story.StoryLineId && row.Id > current.Id)
                .OrderBy(row => row.Id).Select(row => row.Id).FirstOrDefault();
        }
        if (!story.FinishContents.Contains(current.Id))
        {
            story.FinishContents.Add(current.Id);
            RecordMetaProgress(mutation, "StoryContentFinished", value: current.Id);
        }
        story.CurContentId = next;
        story.PveChapterData = null;
        InitializePveState(mutation);
    }

    [RequestPacketHandler("PveStoryLinePromoteRequest")]
    public static void HandlePveStoryLinePromoteRequest(Session session, Packet.Request packet) =>
        Handle<PveStoryLinePromoteRequest, PveStoryLinePromoteResponse>(session, packet, (mutation, request, response) =>
        {
            EnsureAvailable(session, 2);
            InitializePveState(mutation);
            var story = PveStory(mutation, request.StoryLineId);
            Require(mutation.Data.PveAdventureData?.PveChapterData == null, 20282023);
            if (request.ContentId <= 0)
            {
                int teaching = Rows<Theatre5ConfigTable>().Single(row => row.Key == "TeachingPveStoryLineId").Values[0];
                Require(request.StoryLineId == teaching && story.FinishContents.Count == 0, 20282003);
                mutation.Data.CurPveStoryLineId = story.StoryLineId;
            }
            else
            {
                Require(story.CurContentId == request.ContentId, 20282003);
                var content = PveContent(request.ContentId);
                Require(content.ContentType is not (4 or 5), 20282023);
                if (content.ContentType == 1)
                    Require(mutation.Data.PveScripts.TryGetValue(content.ContentId ?? 0, out var script) && script.IsComplete, 20282011);
                CompletePveContent(mutation, story, request.SelectId);
            }
            response.CurContentId = story.CurContentId;
            response.PveAdventureData = Clone(story.PveChapterData);
        });

    [RequestPacketHandler("PveChapterEnterRequest")]
    public static void HandlePveChapterEnterRequest(Session session, Packet.Request packet) =>
        Handle<PveChapterEnterRequest, PveChapterEnterResponse>(session, packet, (mutation, request, response) =>
        {
            EnsureAvailable(session, 2);
            InitializePveState(mutation);
            Require(mutation.Data.PveAdventureData?.PveChapterData == null, 20282016);
            var story = PveStory(mutation, request.StoryLineId);
            var line = Rows<Theatre5PveStoryLineTable>().Single(row => row.Id == story.StoryLineId);
            Require(mutation.Data.PveCharacters.ContainsKey(request.CharacterId), 20282021);
            Require(line.StoryLineCharacter.Contains(request.CharacterId), 20282026);
            int chapterId;
            int entranceId = request.StoryEntranceId ?? 0;
            if (entranceId > 0)
            {
                var entrance = Rows<Theatre5PveStoryEntranceTable>().FirstOrDefault(row => row.Id == entranceId);
                Require(entrance != null && entrance.StoryLine == story.StoryLineId, 20282013);
                Require(entrance.StoryIsOpen == "true" && IsConditionMet(mutation.Session, entrance.BtnOpenCondition ?? 0, mutation.State)
                    && (entrance.BtnCloseCondition == null || !IsConditionMet(mutation.Session, entrance.BtnCloseCondition.Value, mutation.State)), 20282013);
                Require(PveStoryComplete(story), 20282014);
                chapterId = 0;
                for (int i = entrance.RepeatChapter.Count - 1; i >= 0; i--)
                    if (entrance.RepeatChapter[i] > 0 && i < entrance.RepeatChapterCondition.Count
                        && IsConditionMet(mutation.Session, entrance.RepeatChapterCondition[i], mutation.State))
                    { chapterId = entrance.RepeatChapter[i]; break; }
                Require(chapterId > 0, 20282015);
            }
            else
            {
                var content = PveContent(story.CurContentId);
                Require(content.ContentType is 4 or 5, 20282024);
                Require(!content.DisabledCharacters.Contains(request.CharacterId), 20282029);
                chapterId = content.ContentId ?? 0;
            }
            var chapterConfig = Rows<Theatre5PveChapterTable>().FirstOrDefault(row => row.Id == chapterId);
            Require(chapterConfig != null, 20282004);
            mutation.Data.CurStoryEntranceId = entranceId;
            mutation.Data.CurPveStoryLineId = story.StoryLineId;
            story.PveCharacterId = request.CharacterId;
            var adventure = (Theatre5PveAdventureData)InitializeAdventure(mutation, 2, request.CharacterId);
            adventure.Health = chapterConfig.Hp;
            adventure.BagData.RuneGridsNum = chapterConfig.BagRuneGridInitCount;
            var chapter = new Theatre5PveChapterData { ChapterId = chapterId,
                CurPveChapterLevel = new Theatre5PveChapterLevelData { Level = 1 } };
            adventure.PveChapterData = chapter;
            story.PveChapterData = chapter;
            InitializeProgression(mutation);
            mutation.Data.HistoryChapters.TryAdd(chapterId, new Theatre5HistoryChapter { ChapterId = chapterId });
            InitializePveLevel(mutation, chapter);
            response.PveAdventureData = Clone(adventure);
        });

    internal static void ApplyPveBattleResult(Mutation mutation, Theatre5DlcFightResultData report, Theatre5AutoChessGameplayResult result)
    {
        var adventure = mutation.Data.PveAdventureData;
        Require(adventure?.PveChapterData?.CurPveChapterLevel != null, 20282019);
        var chapter = adventure.PveChapterData;
        var level = chapter.CurPveChapterLevel;
        var config = Rows<Theatre5PveChapterTable>().Single(row => row.Id == chapter.ChapterId);
        var story = PveStory(mutation, mutation.Data.CurPveStoryLineId);
        story.PveChapterData = chapter;
        result.BeforeStoryEntranceId = mutation.Data.CurStoryEntranceId;
        if (report.SettleState == 3)
        {
            adventure.Status = 4;
            result.PveChapterData = Clone(chapter);
            result.Health = adventure.Health;
            result.RoundNum = adventure.RoundNum;
            result.CommonFightCnt = Clone(mutation.Data.CommonFightCnt);
            return;
        }
        bool exiting = report.SettleState is 1 or 2;
        bool win = report.IsPlayerWin && !exiting;
        if (!exiting)
        {
            chapter.BattleStatus.Add(win);
            chapter.ContinueWin = win ? checked(chapter.ContinueWin + 1) : 0;
            if (!win) adventure.Health = Math.Max(0, adventure.Health - 1);
            adventure.RoundNum = checked(adventure.RoundNum + 1);
            mutation.Data.CommonFightCnt[adventure.CharacterId] = checked(mutation.Data.CommonFightCnt.GetValueOrDefault(adventure.CharacterId) + 1);
            RecordMetaProgress(mutation, "BattleFinish", adventure.CharacterId);
            if (win)
            {
                mutation.State.CharacterWinCounts[adventure.CharacterId] = checked(mutation.State.CharacterWinCounts.GetValueOrDefault(adventure.CharacterId) + 1);
                RecordMetaProgress(mutation, "BattleWin", adventure.CharacterId);
            }
            TriggerEffects(mutation, win ? "BattleWin" : "BattleLose");
            UpdateMissionProgress(mutation, win ? "BattleWin" : "BattleLose");
            TriggerEffects(mutation, "RoundEnd");
            UpdateMissionProgress(mutation, "RoundEnd");
        }
        int maxLevel = Rows<Theatre5PveChapterLevelTable>().Where(row => row.GroupId == config.LevelGroup).Max(row => row.Level);
        bool passed = win && level.Level == maxLevel;
        bool finished = exiting || adventure.Health <= 0 || passed;
        result.IsFinish = finished;
        if (finished)
        {
            int finishLevel = passed ? level.Level : level.Level - 1;
            var reward = new Theatre5PveRewardShow { IsWin = passed, BaseRewardCoin = config.BaseRewardCoin,
                LevelRewardCoin = config.LevelRewardCoin, FinishLevel = finishLevel, HpRewardCoin = config.HpRewardCoin,
                LeftHp = adventure.Health, LossRewardFactor = config.LossRewardFactor };
            // Matches the authored client breakdown; voluntary exit uses the loss breakdown.
            reward.TotalCoin = checked((passed ? reward.BaseRewardCoin : (int)((long)reward.BaseRewardCoin * reward.LossRewardFactor / 10000))
                + finishLevel * reward.LevelRewardCoin + (passed ? reward.LeftHp * reward.HpRewardCoin : 0));
            result.RewardShow = reward;
            if (reward.TotalCoin > 0)
            {
                int coin = Rows<Theatre5ConfigTable>().Single(row => row.Key == "Theatre5ShopCurrencyId").Values[0];
                mutation.Grant(new RewardGrant(mutation.NextClaimKey(), [new RewardGoodsTable { Id = coin, TemplateId = coin, Count = reward.TotalCoin }]));
            }
            adventure.PveChapterData = null;
            adventure.Status = 2;
            story.PveChapterData = null;
            if (passed)
            {
                mutation.State.ChapterPassCounts[chapter.ChapterId] = checked(mutation.State.ChapterPassCounts.GetValueOrDefault(chapter.ChapterId) + 1);
                if (mutation.Data.CurStoryEntranceId == 0)
                {
                    var content = PveContent(story.CurContentId);
                    bool ready = true;
                    if (content.ContentType == 5)
                    {
                        var script = Rows<Theatre5PveDeduceScriptTable>().FirstOrDefault(row => row.Id == content.NextScript);
                        Require(script != null, 20282042);
                        var prerequisites = Rows<Theatre5PveDeduceClueGroupTable>().Where(row => row.GroupId == script.PreClueGroupId).ToList();
                        Require(prerequisites.Count > 0, 20282042);
                        ready = prerequisites.All(row => mutation.Data.PveClues.ContainsKey(row.ClueId));
                    }
                    // Investigation chapters remain replayable until the next deduction
                    // has its authored clues; advancing early would strand its question UI.
                    if (ready) CompletePveContent(mutation, story);
                }
            }
            result.PveChapterData = null;
        }
        else if (win)
        {
            chapter.CurPveChapterLevel = new Theatre5PveChapterLevelData { Level = level.Level + 1 };
            InitializePveLevel(mutation, chapter);
            result.PveChapterData = Clone(chapter);
        }
        else
        {
            adventure.Status = 7;
            result.PveChapterData = Clone(chapter);
        }
        result.PveStoryLineData = new Theatre5PveStoryLineData { StoryLineId = story.StoryLineId,
            CurContentId = story.CurContentId, FinishContents = [.. story.FinishContents],
            HandleEvents = mutation.State.PveHandledEvents.TryGetValue(story.StoryLineId, out var events) ? [.. events] : [] };
        result.RoundNum = adventure.RoundNum;
        result.Health = adventure.Health;
        result.CheckFailTimes = adventure.CheckFailTimes;
        result.CommonFightCnt = Clone(mutation.Data.CommonFightCnt);
        result.IsCanFreeUnlockGrid = adventure.IsCanFreeUnlockGrid;
    }
}
