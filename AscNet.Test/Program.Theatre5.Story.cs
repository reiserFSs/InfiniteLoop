using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.Table.V2.share.theatre5;
using AscNet.Table.V2.share.theatre5.theatre5pve;
using Newtonsoft.Json.Linq;
using System.Reflection;

namespace AscNet.Test;

internal partial class Program
{
    private static void ValidateGodfallStoryChecks()
    {
        using GodfallCase test = new("authored-story-synthetic-battle-boundary");
        var contents = TableReaderV2.Parse<Theatre5PveStoryLineContentTable>().ToDictionary(row => row.Id);
        var lines = TableReaderV2.Parse<Theatre5PveStoryLineTable>().OrderBy(row => row.Id).ToList();
        var chapters = TableReaderV2.Parse<Theatre5PveChapterTable>().ToDictionary(row => row.Id);
        var levels = TableReaderV2.Parse<Theatre5PveChapterLevelTable>().ToList();
        var events = TableReaderV2.Parse<Theatre5PveEventTable>().ToDictionary(row => row.Id);
        var options = TableReaderV2.Parse<Theatre5PveEventOptionTable>().OrderBy(row => row.Id).ToList();
        var groups = TableReaderV2.Parse<Theatre5PveEventGroupTable>().ToList();
        var scripts = TableReaderV2.Parse<Theatre5PveDeduceScriptTable>().ToDictionary(row => row.Id);
        var questions = TableReaderV2.Parse<Theatre5PveDeduceQuestionTable>().ToList();
        var boxes = TableReaderV2.Parse<Theatre5ItemBoxTable>().ToDictionary(row => row.Id);
        Dictionary<int, int> optionVisits = [];
        Dictionary<int, int> rootVisits = [];
        HashSet<string> calls = [];
        int branchCount = 0, backtrackCount = 0, completedChapters = 0, openedBoxes = 0;
        bool recoveredLoginBox = false;
        if (test.Data.PvpType != 2) Call(nameof(PveOrPvpChangeRequest));
        int teaching = TableReaderV2.Parse<Theatre5ConfigTable>().Single(row => row.Key == "TeachingPveStoryLineId").Values[0];
        Call(nameof(PveStoryLinePromoteRequest), new PveStoryLinePromoteRequest { StoryLineId = teaching });

        foreach (var line in lines)
        {
            AssertEqual(true, test.Data.PveStoryLines.ContainsKey(line.Id), $"Story {line.Id} naturally unlocks from prior endings");
            int transitions = 0;
            while (test.Data.PveStoryLines[line.Id].CurContentId != 0)
            {
                AssertEqual(true, ++transitions <= 256, $"Story {line.Id} finishes without an unbounded investigation replay");
                var story = test.Data.PveStoryLines[line.Id];
                var content = contents[story.CurContentId];
                if (content.ContentType is 4 or 5)
                {
                    int character = line.StoryLineCharacter.First(id => test.Data.PveCharacters.ContainsKey(id)
                        && !content.DisabledCharacters.Contains(id));
                    Call(nameof(PveChapterEnterRequest), new PveChapterEnterRequest { StoryLineId = line.Id, CharacterId = character });
                    AssertEqual(content.ContentId!.Value, test.Data.PveAdventureData!.PveChapterData!.ChapterId, "Chapter entry uses current authored content");
                    CompleteChapter();
                    continue;
                }
                if (content.ContentType == 1)
                {
                    int scriptId = content.ContentId!.Value;
                    AssertEqual(true, test.Data.PveScripts.ContainsKey(scriptId), $"Investigation naturally supplies script {scriptId} prerequisites");
                    var ordered = questions.Where(row => row.GroupId == scripts[scriptId].QuestionGroupId).OrderBy(row => row.Step).ToList();
                    AssertEqual(true, ordered.Count > 0, "Deduction has authored questions");
                    foreach (var question in ordered.Where(row => row.Step > test.Data.PveScripts[scriptId].CurStep))
                    {
                        AssertEqual(true, test.Data.PveClues.ContainsKey(question.AnswerClue), "Deduction answer clue was earned through story/events");
                        int before = test.Data.PveScripts[scriptId].CurStep;
                        Call(nameof(XTheatre5PveAnswerQuestionRequest), new XTheatre5PveAnswerQuestionRequest
                            { ScriptId = scriptId, Step = question.Step, IsCorrect = 0 });
                        AssertEqual(before, test.Data.PveScripts[scriptId].CurStep, "Wrong deduction answer cannot advance the script");
                        var answer = Call(nameof(XTheatre5PveAnswerQuestionRequest), new XTheatre5PveAnswerQuestionRequest
                            { ScriptId = scriptId, Step = question.Step, IsCorrect = 1 });
                        AssertEqual(question.Step, test.Data.PveScripts[scriptId].CurStep, "Correct deduction advances exactly its authored step");
                        AssertEqual(question.Step == ordered[^1].Step, answer.Value<bool>("IsScriptCompleted"), "Only final deduction answer completes script");
                    }
                    AssertEqual(true, test.Data.PveScripts[scriptId].IsComplete, "Deduction completes before story promotion");
                }
                int selection = 0;
                int expectedNext = contents.Values.Where(row => row.StoryLineId == line.Id && row.Id > content.Id)
                    .OrderBy(row => row.Id).Select(row => row.Id).FirstOrDefault();
                if (content.ContentType == 7)
                {
                    selection = content.ChooseContents.Where((id, index) => Condition(content.ChooseConditions[index]))
                        .OrderBy(id => story.FinishContents.Contains(id)).ThenBy(id => id).First();
                    expectedNext = selection;
                    branchCount++;
                }
                else if (content.ContentType == 8)
                {
                    expectedNext = contents.Values.Where(row => row.StoryLineId == line.Id && row.ContentType == 7 && row.Id < content.Id).Max(row => row.Id);
                    backtrackCount++;
                }
                int[] finishedBefore = story.FinishContents.ToArray();
                var promoted = Call(nameof(PveStoryLinePromoteRequest), new PveStoryLinePromoteRequest
                    { StoryLineId = line.Id, ContentId = content.Id, SelectId = selection });
                AssertEqual(expectedNext, promoted.Value<int>("CurContentId"), "Story response follows authored sequential/branch/backtrack target");
                AssertEqual(expectedNext, test.Data.PveStoryLines[line.Id].CurContentId, "Story cursor matches response");
                AssertEqual(true, test.Data.PveStoryLines[line.Id].FinishContents.Contains(content.Id), "Promoted content retained in completion history");
                AssertEqual(true, finishedBefore.All(test.Data.PveStoryLines[line.Id].FinishContents.Contains), "Branch backtrack preserves earlier ending history");
            }
            AssertEqual(true, contents.Values.Where(row => row.StoryLineId == line.Id)
                .All(row => test.Data.PveStoryLines[line.Id].FinishContents.Contains(row.Id)), $"Story {line.Id} covers every authored content including both endings");
        }

        foreach (var entrance in TableReaderV2.Parse<Theatre5PveStoryEntranceTable>().Where(row => row.RepeatChapter.Any(id => id > 0)).OrderBy(row => row.Id))
        {
            var line = lines.Single(row => row.Id == entrance.StoryLine);
            var before = test.Data.PveStoryLines[line.Id].FinishContents.ToArray();
            int expectedChapter = entrance.RepeatChapter.Where((id, index) => id > 0 && Condition(entrance.RepeatChapterCondition[index])).Last();
            Call(nameof(PveChapterEnterRequest), new PveChapterEnterRequest
                { StoryEntranceId = entrance.Id, StoryLineId = line.Id, CharacterId = line.StoryLineCharacter.First(test.Data.PveCharacters.ContainsKey) });
            AssertEqual(expectedChapter, test.Data.PveAdventureData!.PveChapterData!.ChapterId, "Repeat entrance selects authored eligible chapter");
            CompleteChapter();
            AssertEqual(0, test.Data.PveStoryLines[line.Id].CurContentId, "Repeat completion does not restart finished storyline");
            AssertEqual(true, before.SequenceEqual(test.Data.PveStoryLines[line.Id].FinishContents), "Repeat leaves story completion history unchanged");
        }
        AssertEqual(2, branchCount, "Both authored final branch choices traversed");
        AssertEqual(1, backtrackCount, "First ending backtracks once before second ending");
        AssertEqual(true, completedChapters > 0 && openedBoxes > 0, "Natural story reaches chapter settlement and authored box rewards");
        AssertEqual(true, recoveredLoginBox, "Natural event reward exercises disconnect before auto-open and repeated-login recovery");
        foreach (string request in new[] { nameof(PveStoryLinePromoteRequest), nameof(PveEventPromoteRequest), nameof(PveChapterEnterRequest),
            nameof(XTheatre5ItemBoxOpenRequest), nameof(XTheatre5ItemBoxSelectRequest), nameof(PveAvgPlayRequest), nameof(XTheatre5PveAnswerQuestionRequest) })
            AssertEqual(true, calls.Contains(request), $"Natural authored story successfully exercises {request}");

        JObject Call(string request, object? body = null)
        {
            var response = test.Call(request, body);
            calls.Add(request);
            return response;
        }

        bool Condition(int id)
        {
            Type module = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.Theatre5Module");
            return (bool)RequiredMethod(module, "IsConditionMet", BindingFlags.Static | BindingFlags.NonPublic,
                [typeof(Session), typeof(int), typeof(PlayerTheatre5State)]).Invoke(null, [test.Session, id, test.State])!;
        }

        // Decisions are deterministic over authored rows and current state. Server shop/box
        // draws remain random; assertions cover their offered identities, not a fabricated seed.
        IEnumerable<int> Successors(int next, int group) => next > 0 ? new[] { next }
            : (groups ?? throw new InvalidDataException("Authored event groups are unavailable."))
                .Where(row => row.GroupId == group && group > 0).OrderBy(row => row.Id).Select(row => row.EventId);

        int MissingClueDistance(int eventId, HashSet<int> visited)
        {
            if (!visited.Add(eventId)) return 10000;
            var row = events[eventId];
            int clue = Convert.ToInt32(row.Clue);
            if (clue > 0 && !test.Data.PveClues.ContainsKey(clue)) return 0;
            var next = row.Type == 1 ? Successors(Convert.ToInt32(row.NextEvent), Convert.ToInt32(row.NextEventGroup))
                : (options ?? throw new InvalidDataException("Authored event options are unavailable."))
                    .Where(option => option.GroupId == row.OptionGroupId && option.OptionType == 3)
                    .SelectMany(option => Successors(Convert.ToInt32(option.NextEvent), Convert.ToInt32(option.NextEventGroup)));
            int distance = 10000;
            foreach (int id in next) distance = Math.Min(distance, 1 + MissingClueDistance(id, new HashSet<int>(visited)));
            return distance;
        }

        void OpenBoxes()
        {
            for (int guard = 0; guard < 64; guard++)
            {
                var adventure = test.Data.PveAdventureData!;
                var box = adventure.BagData.BagItemDict.Values.Concat(adventure.BagData.TempItemDict.Values)
                    .Where(item => item.ItemType == 3).OrderBy(item => item.InstanceId).FirstOrDefault();
                if (box == null) return;
                if (boxes == null || !boxes.TryGetValue(box.ItemId, out var config))
                    throw new InvalidDataException($"Earned box {box.ItemId} has no authored box definition.");
                if (!recoveredLoginBox && config.IsAutoOpen == 1 && config.BoxOpenType == 1
                    && adventure.BagData.BagItemDict.Values.Concat(adventure.BagData.TempItemDict.Values).Count(item => item.ItemType == 3) == 1)
                {
                    int pendingBefore = adventure.ItemBoxSelectData.Count;
                    var ownedBefore = JToken.FromObject(adventure.BagData.BagItemDict.Values.Concat(adventure.BagData.TempItemDict.Values)
                        .Where(item => item.InstanceId != box.InstanceId).OrderBy(item => item.InstanceId));
                    test.Relog("event-box-before-auto-open", pending: true);
                    AssertEqual(false, test.Harness.TryReadAvailablePacket("silent login box recovery", out _),
                        "Login box recovery sends no orphan event or box packet");
                    var recovered = test.Data.PveAdventureData!;
                    AssertEqual(false, recovered.BagData.BagItemDict.Values.Concat(recovered.BagData.TempItemDict.Values)
                        .Any(item => item.InstanceId == box.InstanceId), "Fresh login consumes the durable physical auto-open box");
                    AssertEqual(pendingBefore + 1, recovered.ItemBoxSelectData.Count, "Fresh login creates exactly one pending selection");
                    var pendingBox = recovered.ItemBoxSelectData.Single(value => value.BoxInstanceId == box.InstanceId);
                    AssertEqual(config.ItemGroupIds.Count, pendingBox.ItemList.Count, "Login selection retains one offered item per authored group");
                    AssertEqual(true, JToken.DeepEquals(ownedBefore, JToken.FromObject(recovered.BagData.BagItemDict.Values
                        .Concat(recovered.BagData.TempItemDict.Values).OrderBy(item => item.InstanceId))),
                        "Login never awards unselected box choices");
                    var pendingSnapshot = JToken.FromObject(recovered.ItemBoxSelectData);
                    test.Relog("event-box-second-login");
                    AssertEqual(false, test.Harness.TryReadAvailablePacket("silent repeated box login", out _),
                        "Repeated login recovery remains silent");
                    AssertEqual(true, JToken.DeepEquals(pendingSnapshot, JToken.FromObject(test.Data.PveAdventureData!.ItemBoxSelectData)),
                        "Second login preserves durable choice identities without reroll or duplicate selection");
                    recoveredLoginBox = true;
                }
                var opened = Call(nameof(XTheatre5ItemBoxOpenRequest), new XTheatre5ItemBoxOpenRequest { BoxInstanceId = box.InstanceId });
                openedBoxes++;
                AssertEqual(config.BoxOpenType, opened.Value<int>("OpenType"), "Box open returns authored open type");
                AssertEqual(box.InstanceId, opened.Value<int>("UsedInstanceId"), "Box open consumes requested instance");
                AssertEqual(true, opened["ItemBoxSelectData"] is JArray, "Box response exposes raw item array");
                var offered = opened["ItemBoxSelectData"]!.ToObject<List<Theatre5Item>>()!;
                AssertEqual(config.ItemGroupIds.Count, offered.Count, "Box has one draw per authored group");
                AssertEqual(false, test.Data.PveAdventureData!.BagData.BagItemDict.Values.Concat(test.Data.PveAdventureData.BagData.TempItemDict.Values)
                    .Any(item => item.InstanceId == box.InstanceId), "Box instance consumed exactly once");
                if (config.BoxOpenType == 1)
                {
                    var reopened = Call(nameof(XTheatre5ItemBoxOpenRequest), new XTheatre5ItemBoxOpenRequest { BoxInstanceId = box.InstanceId });
                    AssertEqual(true, JToken.DeepEquals(opened["ItemBoxSelectData"], reopened["ItemBoxSelectData"]), "Pending box reopen preserves choices without reroll");
                    int selected = offered.OrderBy(item => item.ItemId).ThenBy(item => item.InstanceId).First().InstanceId;
                    Call(nameof(XTheatre5ItemBoxSelectRequest), new XTheatre5ItemBoxSelectRequest { BoxInstanceId = box.InstanceId, ItemInstanceId = selected });
                    AssertEqual(false, test.Data.PveAdventureData!.ItemBoxSelectData.Any(value => value.BoxInstanceId == box.InstanceId), "Selection clears durable pending box");
                    AssertEqual(1, test.Data.PveAdventureData.BagData.BagItemDict.Values.Concat(test.Data.PveAdventureData.BagData.TempItemDict.Values)
                        .Count(item => item.InstanceId == selected), "Exactly one selected instance awarded");
                    AssertEqual(false, test.Data.PveAdventureData.BagData.BagItemDict.Values.Concat(test.Data.PveAdventureData.BagData.TempItemDict.Values)
                        .Any(item => offered.Any(choice => choice.InstanceId != selected && choice.InstanceId == item.InstanceId)), "Unselected box choices not awarded");
                }
                else
                    foreach (var item in offered)
                        AssertEqual(1, test.Data.PveAdventureData!.BagData.BagItemDict.Values.Concat(test.Data.PveAdventureData.BagData.TempItemDict.Values)
                            .Count(owned => owned.InstanceId == item.InstanceId), "All-open box awards each offered instance once");
            }
            throw new InvalidDataException("Authored story box chain did not terminate.");
        }

        void CompleteChapter()
        {
            int chapterId = test.Data.PveAdventureData!.PveChapterData!.ChapterId;
            var config = chapters[chapterId];
            int maximum = levels.Where(row => row.GroupId == config.LevelGroup).Max(row => row.Level);
            int passes = test.State.ChapterPassCounts.GetValueOrDefault(chapterId);
            if (!string.IsNullOrEmpty(config.StartStory))
            {
                Call(nameof(PveAvgPlayRequest), new PveAvgPlayRequest { ChapterId = chapterId, IsEnterAvg = true });
                AssertEqual(true, test.Data.HistoryChapters[chapterId].IsEnterAvgPlay, "Chapter opening AVG acknowledged before events");
            }
            for (int expectedLevel = 1; expectedLevel <= maximum; expectedLevel++)
            {
                var level = test.Data.PveAdventureData!.PveChapterData!.CurPveChapterLevel!;
                AssertEqual(expectedLevel, level.Level, "Synthetic win advances one authored chapter level");
                var authoredLevel = levels.Single(row => row.GroupId == config.LevelGroup && row.Level == expectedLevel);
                AssertEqual(true, level.RandomEvents.SequenceEqual(groups.Where(row => row.GroupId == authoredLevel.EventGroup)
                    .OrderBy(row => row.Id).Select(row => row.EventId).Distinct()), "Event offers use current chapter's authored group");
                int root = level.RandomEvents.OrderBy(id => MissingClueDistance(id, []))
                    .ThenBy(id => rootVisits.GetValueOrDefault(id)).ThenBy(id => id).First();
                rootVisits[root] = rootVisits.GetValueOrDefault(root) + 1;
                var selectionBefore = JToken.FromObject(new
                {
                    test.Adventure.GoldNum, test.Adventure.CharacterExp, test.Adventure.BagData,
                    test.Data.PveClues, test.Data.HistoryChapters[chapterId].FinishEvents,
                    test.Data.PveAdventureData!.PveChapterData!.HandleEvents
                });
                var selected = Call(nameof(PveEventPromoteRequest), new PveEventPromoteRequest { EventId = root });
                AssertEqual(root, selected.Value<int>("NextEventId"), "Initial map selection opens root without executing it");
                AssertEqual(0, selected.Value<int>("ClueId"), "Initial map selection awards no clue");
                AssertEqual(0, selected.Value<int>("Exp"), "Initial map selection awards no experience");
                AssertEqual(true, selected["PveEventReward"]?.Type is null or JTokenType.Null
                    && selected["ExtPveEventReward"]?.Type is null or JTokenType.Null, "Initial map selection awards no event rewards");
                AssertEqual(1, test.Adventure.Status, "Initial selection remains in event state");
                AssertEqual(true, test.Data.PveAdventureData!.PveChapterData!.CurPveChapterLevel!.RunEvents.SequenceEqual(new[] { root }),
                    "Initial selection retains exactly the root for its separate completion request");
                AssertEqual(true, JToken.DeepEquals(selectionBefore, JToken.FromObject(new
                {
                    test.Adventure.GoldNum, test.Adventure.CharacterExp, test.Adventure.BagData,
                    test.Data.PveClues, test.Data.HistoryChapters[chapterId].FinishEvents,
                    test.Data.PveAdventureData!.PveChapterData!.HandleEvents
                })), "Selecting a map root changes no rewards, clues, or event completion history");
                int eventId = root;
                for (int step = 0; ; step++)
                {
                    AssertEqual(true, step < 128, "Authored event chain terminates");
                    var row = events[eventId];
                    int optionId = 0;
                    int expectedNext = Convert.ToInt32(row.NextEvent), expectedGroup = Convert.ToInt32(row.NextEventGroup);
                    if (row.Type == 2)
                    {
                        // Every authored choice group on this route has a free option;
                        // never refill chapter currency to force a paid option.
                        var option = options.Where(value => value.GroupId == row.OptionGroupId && value.OptionType == 3)
                            .OrderBy(value => Successors(Convert.ToInt32(value.NextEvent), Convert.ToInt32(value.NextEventGroup))
                                .Select(id => MissingClueDistance(id, [])).DefaultIfEmpty(10000).Min())
                            .ThenBy(value => Convert.ToInt32(value.NextEventGroup) > 0)
                            .ThenBy(value => optionVisits.GetValueOrDefault(value.Id)).ThenBy(value => value.Id).First();
                        optionId = option.Id;
                        optionVisits[optionId] = optionVisits.GetValueOrDefault(optionId) + 1;
                        expectedNext = Convert.ToInt32(option.NextEvent);
                        expectedGroup = Convert.ToInt32(option.NextEventGroup);
                    }
                    var response = Call(nameof(PveEventPromoteRequest), new PveEventPromoteRequest { EventId = eventId, OptionId = optionId });
                    int next = response.Value<int>("NextEventId");
                    if (expectedGroup == 0) AssertEqual(expectedNext, next, "Event transition uses selected authored successor");
                    else AssertEqual(true, Successors(0, expectedGroup).Contains(next), "Random successor belongs to authored group");
                    int clue = Convert.ToInt32(row.Clue);
                    AssertEqual(clue, response.Value<int>("ClueId"), "Event response exposes its authored clue");
                    if (clue > 0) AssertEqual(true, test.Data.PveClues.ContainsKey(clue), "Event clue persisted for deduction");
                    AssertEqual(true, test.Data.PveAdventureData!.PveChapterData!.HandleEvents.Contains(eventId), "Event completion retained in chapter history");
                    OpenBoxes();
                    if (next == 0) break;
                    eventId = next;
                }
                AssertEqual(2, test.Data.PveAdventureData!.Status, "Terminal event opens preparation state");
                AssertEqual(true, test.Data.HistoryChapters[chapterId].FinishEvents.Contains(root), "Completed root retained for bonus-once policy");
                Call(nameof(Theatre5EnterShopRequest));
                GodfallPrepareBattle(test);
                Call(nameof(DlcSingleEnterFightRequest), new DlcSingleEnterFightRequest { WorldId = 200, LevelId = expectedLevel });
                if (completedChapters == 0 && expectedLevel == 1)
                {
                    GodfallCheckCrossModeAttempt(test);
                    GodfallCheckPveNerfLevels(test);
                }
                // Synthetic source-shaped report validates the protocol/state boundary only.
                // No engine execution, native combat fidelity, or achievable victory is claimed.
                Call(nameof(DlcSingleFightSettleRequest), GodfallSyntheticNativeResult(test));
            }
            AssertEqual(true, test.Data.PveAdventureData!.PveChapterData == null, "Final synthetic win clears active chapter");
            AssertEqual(passes + 1, test.State.ChapterPassCounts[chapterId], "Chapter completion recorded exactly once");
            if (!string.IsNullOrEmpty(config.EndStory))
            {
                Call(nameof(PveAvgPlayRequest), new PveAvgPlayRequest { ChapterId = chapterId, IsEnterAvg = false });
                AssertEqual(true, test.Data.HistoryChapters[chapterId].IsPassAvgPlay, "Completed chapter ending AVG acknowledged");
            }
            completedChapters++;
        }
    }
}
