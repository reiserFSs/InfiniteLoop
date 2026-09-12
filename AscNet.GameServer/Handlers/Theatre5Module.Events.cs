using AscNet.Common.MsgPack;
using AscNet.Table.V2.share.theatre5;
using AscNet.Table.V2.share.theatre5.theatre5pve;

namespace AscNet.GameServer.Handlers;

internal static partial class Theatre5Module
{
    internal static void InitializePveLevel(Mutation m, Theatre5PveChapterData chapter)
    {
        Require(chapter.CurPveChapterLevel != null, 20282020);
        var level = chapter.CurPveChapterLevel;
        var config = PveEventChapterLevel(chapter);
        // The authored group defines eligibility, not a smaller offer count. Keep its
        // ordered distinct roots; the map lets the player choose before the first RPC.
        level.RandomEvents = PveEventGroup(config.EventGroup);
        level.RunEvents.Clear();
        level.RandomEventCnt.Clear();
        foreach (int eventId in level.RandomEvents)
            if (!chapter.HasRandomEvents.Contains(eventId)) chapter.HasRandomEvents.Add(eventId);
        m.Data.HistoryChapters.TryAdd(chapter.ChapterId, new() { ChapterId = chapter.ChapterId });
        Require(m.Data.PveAdventureData != null, 20282022);
        m.Data.PveAdventureData.Status = 1;
    }

    private static Theatre5PveChapterLevelTable PveEventChapterLevel(Theatre5PveChapterData chapter)
    {
        var chapterConfig = Rows<Theatre5PveChapterTable>().FirstOrDefault(row => row.Id == chapter.ChapterId);
        Require(chapterConfig != null, 20282004);
        Require(chapter.CurPveChapterLevel != null, 20282020);
        var level = Rows<Theatre5PveChapterLevelTable>().FirstOrDefault(row =>
            row.GroupId == chapterConfig.LevelGroup && row.Level == chapter.CurPveChapterLevel.Level);
        Require(level != null, 20282005);
        return level;
    }

    private static List<int> PveEventGroup(int groupId)
    {
        var events = Rows<Theatre5PveEventGroupTable>().Where(row => row.GroupId == groupId)
            .OrderBy(row => row.Id).Select(row => row.EventId).Distinct().ToList();
        Require(events.Count > 0, 20282006);
        var authored = Rows<Theatre5PveEventTable>();
        Require(events.All(id => authored.Any(row => row.Id == id)), 20282007);
        return events;
    }

    [RequestPacketHandler("PveEventPromoteRequest")]
    public static void PveEventPromote(Session session, Packet.Request packet) =>
        Handle<PveEventPromoteRequest, PveEventPromoteResponse>(session, packet, (m, request, response) =>
        {
            EnsureAvailable(session, 2);
            Require(m.Data.PvpType == 2, 20282001);
            var adventure = m.Data.PveAdventureData;
            Require(adventure != null, 20282022);
            Require(adventure.Status == 1, 20282033);
            var chapter = adventure.PveChapterData;
            Require(chapter != null, 20282019);
            var level = chapter.CurPveChapterLevel;
            Require(level != null, 20282020);
            Require(adventure.ItemBoxSelectData.Count == 0, 20280006);
            Require(m.Data.HistoryChapters.TryGetValue(chapter.ChapterId, out var history), 20282012);
            var chapterConfig = Rows<Theatre5PveChapterTable>().FirstOrDefault(row => row.Id == chapter.ChapterId);
            Require(chapterConfig != null, 20282004);
            Require(level.Level != 1 || string.IsNullOrEmpty(chapterConfig.StartStory) || history.IsEnterAvgPlay, 20280006);
            if (level.RunEvents.Count == 0)
            {
                Require(level.RandomEvents.Contains(request.EventId), 20282031);
                Require(request.OptionId.GetValueOrDefault() == 0, 20282034);
                level.RunEvents.Add(request.EventId);
                if (!chapter.HasSelectedEvents.Contains(request.EventId)) chapter.HasSelectedEvents.Add(request.EventId);
                Require(m.Data.PveStoryLines.TryGetValue(m.Data.CurPveStoryLineId, out var selectedStory), 20282018);
                selectedStory.PveChapterData = chapter;
                // The map selection opens the event UI; only its subsequent request
                // executes the chat/option and awards the event's authored outcome.
                response.NextEventId = request.EventId;
                return;
            }
            else Require(level.RunEvents[0] == request.EventId, 20282032);

            var config = Rows<Theatre5PveEventTable>().FirstOrDefault(row => row.Id == request.EventId);
            Require(config != null, 20282007);
            int next = Convert.ToInt32(config.NextEvent), nextGroup = Convert.ToInt32(config.NextEventGroup);
            if (config.Type == 1)
                Require(request.OptionId.GetValueOrDefault() == 0, 20282034);
            else
            {
                Require(config.Type == 2, 20282007);
                var option = Rows<Theatre5PveEventOptionTable>().FirstOrDefault(row => row.Id == request.OptionId);
                Require(option != null, 20282008);
                Require(option.GroupId == config.OptionGroupId, 20282034);
                ApplyPveEventOption(m, option);
                next = Convert.ToInt32(option.NextEvent);
                nextGroup = Convert.ToInt32(option.NextEventGroup);
            }
            Require(next == 0 || nextGroup == 0, 20282009);
            if (nextGroup > 0)
            {
                var candidates = PveEventGroup(nextGroup);
                // Local policy: unweighted authored successor groups draw uniformly.
                // The mutation journal freezes the outcome; retries never reroll it.
                next = candidates[Random.Shared.Next(candidates.Count)];
                level.RandomEventCnt[next] = checked(level.RandomEventCnt.GetValueOrDefault(next) + 1);
            }
            Require(next >= 0 && (next == 0 || Rows<Theatre5PveEventTable>().Any(row => row.Id == next)), 20282009);

            int root = level.RunEvents[^1];
            int expBefore = adventure.CharacterExp;
            int rewardGroup = Convert.ToInt32(config.EventLevelGroup);
            if (rewardGroup > 0)
            {
                var levelConfig = PveEventChapterLevel(chapter);
                var reward = Rows<Theatre5PveEventLevelTable>().FirstOrDefault(row =>
                    row.Group == rewardGroup && row.EventLevel == levelConfig.EventLevel);
                Require(reward != null, 20282010);
                response.PveEventReward = GivePveEventReward(m, reward.ItemType, reward.ItemId, reward.ItemCount);
                if (!history.FinishEvents.Contains(root))
                    response.ExtPveEventReward = GivePveEventReward(m, reward.BonusItemTypes, reward.BonusItemIds, reward.BonusItemCounts);
            }
            int clueId = Convert.ToInt32(config.Clue);
            if (clueId > 0)
            {
                var clue = Rows<Theatre5PveDeduceClueTable>().FirstOrDefault(row => row.Id == clueId);
                Require(clue != null && clue.Type == 2, 20282039);
                m.Data.PveClues.TryAdd(clueId, new() { ClueId = clueId });
                response.ClueId = clueId;
            }
            chapter.HandleEvents.Add(config.Id);
            if (!m.State.PveHandledEvents.TryGetValue(m.Data.CurPveStoryLineId, out var handledEvents))
                m.State.PveHandledEvents[m.Data.CurPveStoryLineId] = handledEvents = [];
            handledEvents.Add(config.Id);
            if (next > 0) level.RunEvents.Insert(0, next);
            else
            {
                if (!history.FinishEvents.Contains(root)) history.FinishEvents.Add(root);
                adventure.Status = 2;
            }
            response.NextEventId = next;
            // Current EN events contain no authored Exp column. Any item EXP remains
            // a delta here, never an absolute AddExp effect plus the same delta twice.
            response.Exp = checked(adventure.CharacterExp - expBefore);
            Require(m.Data.PveStoryLines.TryGetValue(m.Data.CurPveStoryLineId, out var story), 20282018);
            story.PveChapterData = chapter;
            UnlockPveKnowledge(m);
            PushBag(m);
        });

    private static void ApplyPveEventOption(Mutation m, Theatre5PveEventOptionTable option)
    {
        if (option.OptionType == 3) return;
        Require(option.OptionType is 1 or 2, 20282034);
        int type = Convert.ToInt32(option.OptionCostType);
        int id = Convert.ToInt32(option.OptionCostId);
        int count = Convert.ToInt32(option.OptionCostCount);
        Require(id > 0 && count > 0, 20282036);
        if (type == 4)
        {
            Require(id == ConfigInt("ChapterCoin"), 20282036);
            var adventure = Adventure(m);
            Require(adventure.GoldNum >= count, option.OptionType == 1 ? 20282037 : 20282038);
            if (option.OptionType == 1) adventure.GoldNum -= count;
        }
        else if (type == 5)
        {
            Require(option.OptionType == 2 && Rows<Theatre5PveDeduceClueTable>().Any(row => row.Id == id), 20282036);
            Require(m.Data.PveClues.ContainsKey(id), 20282038);
        }
        else
        {
            Require(type is 1 or 2 or 3 or 6 or 7, 20282036);
            Require(Rows<Theatre5ItemTable>().Any(row => row.Id == id && row.Type == type), 20282039);
            Require(HasItems(m, id, count), option.OptionType == 1 ? 20282037 : 20282038);
            if (option.OptionType == 1) SpendItems(m, id, count);
        }
    }

    private static Theatre5EventReward GivePveEventReward(Mutation m, List<int> types, List<int> ids, List<int> counts)
    {
        Require(types.Count == ids.Count && types.Count == counts.Count, 20282039);
        Theatre5EventReward result = new();
        for (int i = 0; i < types.Count; i++)
        {
            if (types[i] == 0 && ids[i] == 0 && counts[i] == 0) continue;
            Require(ids[i] > 0 && counts[i] > 0, 20282039);
            if (types[i] == 4)
            {
                Require(ids[i] == ConfigInt("ChapterCoin"), 20282039);
                Adventure(m).GoldNum = checked(Adventure(m).GoldNum + counts[i]);
                result.GoldNum = checked(result.GoldNum + counts[i]);
            }
            else
            {
                Require(Rows<Theatre5ItemTable>().Any(row => row.Id == ids[i] && row.Type == types[i]), 20282039);
                result.Items.AddRange(AddItems(m, ids[i], counts[i]));
            }
        }
        return result;
    }

    [RequestPacketHandler("PveAvgPlayRequest")]
    public static void PveAvgPlay(Session session, Packet.Request packet) =>
        Handle<PveAvgPlayRequest, PveAvgPlayResponse>(session, packet, (m, request, response) =>
        {
            EnsureAvailable(session, 2);
            Require(m.Data.PvpType == 2, 20282001);
            var config = Rows<Theatre5PveChapterTable>().FirstOrDefault(row => row.Id == request.ChapterId);
            Require(config != null, 20282004);
            Require(m.Data.HistoryChapters.TryGetValue(request.ChapterId, out var history), 20282012);
            if (request.IsEnterAvg)
            {
                Require(!string.IsNullOrEmpty(config.StartStory), 20282004);
                if (history.IsEnterAvgPlay) return;
                var adventure = m.Data.PveAdventureData;
                var chapter = adventure?.PveChapterData;
                Require(chapter != null && chapter.ChapterId == request.ChapterId
                    && chapter.CurPveChapterLevel?.Level == 1 && adventure!.Status == 1
                    && chapter.CurPveChapterLevel.RunEvents.Count == 0, 20280006);
                history.IsEnterAvgPlay = true;
            }
            else
            {
                Require(!string.IsNullOrEmpty(config.EndStory), 20282004);
                Require(m.State.ChapterPassCounts.GetValueOrDefault(request.ChapterId) > 0, 20280006);
                history.IsPassAvgPlay = true;
            }
        });

    internal static void UnlockPveKnowledge(Mutation m)
    {
        var clues = Rows<Theatre5PveDeduceClueTable>();
        foreach (var clue in clues.Where(row => row.Type == 1))
        {
            if (!m.Data.PveStoryLines.Values.Any(story => story.CurContentId == clue.OpenStoryLineContentId
                || story.FinishContents.Contains(clue.OpenStoryLineContentId))) continue;
            int scriptId = Convert.ToInt32(clue.ScriptId);
            m.Data.PveClues.TryAdd(clue.Id, new() { ClueId = clue.Id, IsComplete = scriptId == 0 });
        }
        var groups = Rows<Theatre5PveDeduceClueGroupTable>();
        foreach (var script in Rows<Theatre5PveDeduceScriptTable>())
        {
            if (!clues.Any(clue => clue.ScriptId == script.Id && m.Data.PveClues.ContainsKey(clue.Id))) continue;
            var prerequisites = groups.Where(row => row.GroupId == script.PreClueGroupId).ToList();
            if (prerequisites.Count == 0 || !prerequisites.All(row => m.Data.PveClues.ContainsKey(row.ClueId))) continue;
            m.Data.PveScripts.TryAdd(script.Id, new() { ScriptId = script.Id });
        }
    }

    [RequestPacketHandler("XTheatre5PveAnswerQuestionRequest")]
    public static void PveAnswerQuestion(Session session, Packet.Request packet) =>
        Handle<XTheatre5PveAnswerQuestionRequest, XTheatre5PveAnswerQuestionResponse>(session, packet, (m, request, response) =>
        {
            EnsureAvailable(session, 2);
            Require(m.Data.PvpType == 2, 20282001);
            Require(request.IsCorrect is 0 or 1, 20282043);
            var config = Rows<Theatre5PveDeduceScriptTable>().FirstOrDefault(row => row.Id == request.ScriptId);
            Require(config != null, 20282042);
            UnlockPveKnowledge(m);
            Require(m.Data.PveScripts.TryGetValue(request.ScriptId, out var script), 20282040);
            Require(!script.IsComplete, 20282041);
            var questions = Rows<Theatre5PveDeduceQuestionTable>().Where(row => row.GroupId == config.QuestionGroupId)
                .OrderBy(row => row.Step).ToList();
            var question = questions.FirstOrDefault(row => row.Step > script.CurStep);
            Require(question != null && question.Step == request.Step, 20282043);
            Require(m.Data.PveClues.ContainsKey(question.AnswerClue), 20282040);
            Require(Rows<Theatre5PveDeduceClueGroupTable>().Any(row =>
                row.GroupId == question.ShowClueGroupId && row.ClueId == question.AnswerClue), 20282043);
            if (request.IsCorrect == 0) return;
            // The native contract reports correctness numerically, not a selected clue.
            // Server enforces unlocked prerequisites and order; it cannot infer an answer ID.
            script.CurStep = question.Step;
            script.IsComplete = question.Step == questions[^1].Step;
            response.IsScriptCompleted = script.IsComplete;
            if (script.IsComplete)
            {
                foreach (var prerequisite in Rows<Theatre5PveDeduceClueGroupTable>().Where(row => row.GroupId == config.PreClueGroupId))
                {
                    Require(m.Data.PveClues.TryGetValue(prerequisite.ClueId, out var clue), 20282040);
                    clue.IsComplete = true;
                }
                foreach (var clue in Rows<Theatre5PveDeduceClueTable>().Where(row => row.ScriptId == script.ScriptId))
                    m.Data.PveClues[clue.Id] = new() { ClueId = clue.Id, IsComplete = true };
                UnlockPveKnowledge(m);
            }
        });
}
