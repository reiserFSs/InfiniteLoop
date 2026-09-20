using AscNet.Common.Database;
using AscNet.Common.Util;
using MongoDB.Bson;
using AscNet.Table.V2.share.condition;
using AscNet.Table.V2.share.theatre6;
using Newtonsoft.Json.Linq;

namespace AscNet.Test;

// Natural story/gameplay traversal. Every state read comes from the wire snapshot (start and
// continue responses plus NotifyTheatre6NewRoomData/NewFloorData) and every step is a registered
// request carrying the exact client key set; the chain order is taken from the authored
// stage/floor/room tables, so a skipped, duplicated or re-rolled room changes observable state.
internal partial class Program
{
    private static void ValidateRequiemStoryAndGameplayChecks()
    {
        using RequiemCase test = new("story-gameplay");
        int guideId = test.CommonGuideStoryId();
        test.Call("Theatre6StoryModeGuideFinishedRequest", Req(("StoryId", guideId)));
        AssertEqual(true, test.Login()["StoryModeSaveDb"]!.Value<JArray>("StoryIds")!.Any(token => token.Value<int>() == guideId),
            "Theatre6 common guide acknowledgement records the configured story id");
        test.Call("Theatre6StoryModeGuideFinishedRequest", Req(("StoryId", guideId)));
        AssertEqual(1, test.Login()["StoryModeSaveDb"]!.Value<JArray>("StoryIds")!.Count(token => token.Value<int>() == guideId),
            "Repeated Theatre6 guide acknowledgement is idempotent");

        // Gameplay is gated by the authored stage-progress condition, so it must be rejected until
        // the common storyline has been advanced.
        (int character, int fashion, int buff, int difficulty) = RequiemBuild(0, 0);
        test.Reject("Theatre6PlayModeStartFightRequest", Req(("CharacterId", character), ("FashionId", fashion),
            ("InitBuffId", buff), ("DifficultyId", difficulty)), "gameplay before the authored unlock condition");
        AssertEqual(0, test.State.ActiveRuns.Count, "Rejected Theatre6 gameplay start creates no run");

        int storyStage = test.StartStoryRun(0);
        JObject storyStart = test.Mode(RequiemCase.StoryModeKey);
        AssertEqual(2, storyStart.Value<int>("ModeId"), "Theatre6 story run reports story mode");
        AssertEqual(storyStage, storyStart.Value<int>("StageId"), "Theatre6 common story starts from the authored common stage");
        Require(storyStart["CurrentRoomDataDb"] is JObject, "Theatre6 story start carries the current room snapshot.");
        AssertEqual(RequiemAuthoredStageField(storyStage, "HpNum"), storyStart.Value<int>("MaxHealth"),
            "Theatre6 story starting health derives from the authored stage row");
        AssertEqual(RequiemAuthoredStageField(storyStage, "BaseSan"), storyStart.Value<int>("MaxSan"),
            "Theatre6 story starting sanity derives from the stage row");

        // Suspend/resume: ContinueGame must restore the same frozen run and room identity.
        string frozenRoom = test.RoomIdentity(RequiemCase.StoryModeKey);
        int runId = test.ActiveRunId;
        byte[] beforeRelog = test.State.ToBson();
        test.Relog("story-mid-run");
        AssertEqual(Convert.ToHexString(beforeRelog), Convert.ToHexString(test.State.ToBson()),
            "Theatre6 mid-run login cannot mutate the suspended story run");
        Require(test.Login()[RequiemCase.StoryModeKey] is JObject, "Theatre6 mid-run login republishes the suspended story snapshot.");
        test.Call("Theatre6ContinueGameRequest", Req(("ModeId", 2)));
        AssertEqual(runId, test.ActiveRunId, "Theatre6 ContinueGame resumes the same frozen run");
        AssertEqual(frozenRoom, test.RoomIdentity(RequiemCase.StoryModeKey), "Theatre6 ContinueGame cannot re-roll the frozen room");

        bool storyExtraFloor = RequiemStageHasExtraFloor(storyStage);
        test.WalkRun(RequiemCase.StoryModeKey, "common-story");
        AssertEqual(storyExtraFloor, test.ConsumedExtraFloor, "Theatre6 common story follows the authored extra-floor gate");
        JObject firstSettle = test.AcknowledgeRun(RequiemCase.StoryModeKey, modeId: 2, slot: test.NextFreeSaveSlot());
        AssertEqual(true, firstSettle.Value<bool>("IsWin"), "Theatre6 common story clears naturally");
        JArray firstRewards = firstSettle["RewardList"] as JArray ?? [];
        Require(firstRewards.Count > 0, "Theatre6 first clear must report the authored settlement rewards.");
        test.AssertBuffRewardShape(firstRewards);
        int firstRewardTotal = firstRewards.Sum(reward => reward.Value<int>("Count"));
        AssertEqual(1, test.ArchiveCount(), "Theatre6 common story archive acknowledgement stores exactly one archive");
        Require(test.State.PassStageRecords.Values.Any(passed => passed > 0),
            "Theatre6 story clear records authored stage progress for the gameplay gate in durable state");
        int coin = test.StoryLineCoin();
        long firstTokenGrant = firstRewards.Where(reward => reward.Value<int>("Id") == coin)
            .Sum(reward => (long)reward.Value<int>("Count"));
        long coinsAfterClear = test.Balance(coin);
        Require(firstTokenGrant >= 0, "Theatre6 first clear reports its authored token rewards.");

        // Replay of the same cleared line: progress is max-merged and first-clear rewards are not repaid.
        test.StartStoryRun(0, storyStage);
        test.WalkRun(RequiemCase.StoryModeKey, "common-replay", confirmExtraFloor: false);
        JObject replaySettle = test.AcknowledgeRun(RequiemCase.StoryModeKey, modeId: 2, slot: 0);
        AssertEqual(true, replaySettle.Value<bool>("IsWin"), "Theatre6 common story replay clears naturally");
        if (storyExtraFloor)
            AssertEqual(true, test.DeclinedExtraFloor, "Theatre6 replay exercises the authored extra-floor decline");
        JArray replayRewards = replaySettle["RewardList"] as JArray ?? [];
        AssertEqual(0, replayRewards.Count(reward => reward.Value<bool>("IsFirst")),
            "Theatre6 replay does not repay first-clear settlement rewards");
        // The wallet must move by exactly the authored repeat rewards the settlement itself lists.
        long replayTokenGrant = replayRewards.Where(reward => reward.Value<int>("Id") == coin)
            .Sum(reward => (long)reward.Value<int>("Count"));
        AssertEqual(coinsAfterClear + replayTokenGrant, test.Balance(coin),
            $"Theatre6 replay grants only the authored repeat rewards it lists ({replayTokenGrant} tokens)");
        AssertEqual(1, test.ArchiveCount(), "Theatre6 story replay does not mint a second archive");
        AssertEqual(true, firstRewardTotal > 0, "Theatre6 first clear reports a non-empty authored reward total");

        // Paid storyline. The authored ConsumeCounts of every line leaves column 1 blank, and the installed client
        // reads config.ConsumeCounts[stageIndex] through XTool.IsNumberValid, which rejects nil and 0
        // (XTool.lua:577), so IsStageNeedPurchase (XTheatre6Model:350-352) is false for the line's first stage: it is
        // free, and the first chargeable stage is the first authored count above zero. Entry charges exactly that
        // stage's authored count, and the client reads the purchase back as the stage's zero-based index in BuyIndex
        // (XTheatre6Model:345-353, IsStoryLineStageHasBuy compares table.contains(data.BuyIndex, stageIndex - 1)).
        int paidLine = test.PaidStoryLine();
        int paidStageIndex = test.PaidStoryStageIndex(paidLine);
        int paidPrice = test.PaidStoryStagePrice(paidLine, paidStageIndex);
        Require(paidStageIndex > 0 && paidPrice > 0,
            "Theatre6 paid story fixture requires an authored chargeable stage after the free first stage.");

        // Stage index 0 is authored free: its entry charges nothing and records no purchase.
        long coinsBeforeFreeStage = test.Balance(coin);
        test.StartStoryRun(test.StoryLineIndex(paidLine));
        AssertEqual(coinsBeforeFreeStage, test.Balance(coin),
            "Theatre6 storyline stage 1 is authored free and charges no token");
        AssertEqual(0, test.BoughtStageIndexes(paidLine).Count,
            "Theatre6 free storyline stage records no purchase");
        test.WalkRun(RequiemCase.StoryModeKey, "paid-free-stage");
        JObject freeStageSettle = test.AcknowledgeRun(RequiemCase.StoryModeKey, modeId: 2, slot: 0);
        AssertEqual(true, freeStageSettle.Value<bool>("IsWin"), "Theatre6 free storyline stage clears naturally");

        // The first authored chargeable stage charges its own count exactly once and records its zero-based index.
        Require(test.Balance(coin) > paidPrice,
            "Theatre6 paid story fixture requires storyline tokens earned by clearing the free stage.");
        long coinsBeforePaidStage = test.Balance(coin);
        test.StartStoryRun(test.StoryLineIndex(paidLine));
        Require(test.BoughtStageIndexes(paidLine).Contains(paidStageIndex),
            $"Paid Theatre6 story entry records the client's zero-based bought stage index {paidStageIndex}");
        AssertEqual(coinsBeforePaidStage - paidPrice, test.Balance(coin),
            $"Paid Theatre6 story entry charges the authored {paidPrice} storyline token(s) once");
        test.WalkRun(RequiemCase.StoryModeKey, "paid-story");
        JObject paidSettle = test.AcknowledgeRun(RequiemCase.StoryModeKey, modeId: 2, slot: test.NextFreeSaveSlot());
        AssertEqual(true, paidSettle.Value<bool>("IsWin"), "Paid Theatre6 story line clears naturally");
        AssertEqual(2, test.ArchiveCount(), "Paid Theatre6 story acknowledgement stores the line's own archive");

        // Charging is per stage: the next authored chargeable stage is charged for itself while every earlier
        // purchase stays recorded, and abandoning the run keeps both the purchase and the archive count.
        int nextPaidStageIndex = paidStageIndex + 1;
        int nextPaidPrice = test.PaidStoryStagePrice(paidLine, nextPaidStageIndex);
        Require(nextPaidPrice > 0, "Theatre6 paid story fixture requires a second authored chargeable stage.");
        long coinsBeforeNextStage = test.Balance(coin);
        test.StartStoryRun(test.StoryLineIndex(paidLine));
        List<int> boughtAfterNextEntry = test.BoughtStageIndexes(paidLine);
        Require(boughtAfterNextEntry.Contains(paidStageIndex) && boughtAfterNextEntry.Contains(nextPaidStageIndex),
            "Theatre6 paid story keeps earlier purchases and records the next chargeable stage index");
        AssertEqual(coinsBeforeNextStage - nextPaidPrice, test.Balance(coin),
            "Theatre6 paid story charges only the newly entered chargeable stage");
        test.AbandonRun(RequiemCase.StoryModeKey, modeId: 2);
        AssertEqual(2, test.ArchiveCount(), "Theatre6 abandoned paid story run adds no archive");

        // Builds: two table-selected character/difficulty pairs plus the authored extended tier
        // whose ladder carries an extra floor, exercised with the entry accepted and declined.
        (int secondCharacter, int secondFashion, int secondBuff, int secondDifficulty) = RequiemBuild(1, 1);
        int extendedDifficulty = RequiemExtendedDifficulty();
        (int extendedCharacter, int extendedFashion, int extendedBuff, int extendedResolved) = RequiemExtendedBuild(extendedDifficulty);
        Require(RequiemAuthoredExtraFloor(extendedDifficulty),
            "Theatre6 extended tier fixture must carry an authored extra floor.");
        (int buildCharacter, int buildFashion, int buildBuff, int buildDifficulty, bool confirmExtraFloor, bool expectGate)[] builds =
        [
            (character, fashion, buff, difficulty, true, false),
            (secondCharacter, secondFashion, secondBuff, secondDifficulty, true, false)
        ];
        foreach ((int buildCharacter, int buildFashion, int buildBuff, int buildDifficulty, bool confirmExtraFloor, bool expectGate) in builds)
        {
            using RequiemCase build = new($"gameplay-{buildCharacter}-{buildDifficulty}-{confirmExtraFloor}");
            build.UnlockGameplay();
            RequiemSatisfyDifficultyChain(build, buildDifficulty);
            string modeKey = build.StartGameplayRun(buildCharacter, buildFashion, buildBuff, buildDifficulty);
            JObject start = build.Mode(modeKey);
            AssertEqual(buildCharacter, start.Value<int>("CharacterId"), "Theatre6 gameplay start keeps the chosen character");
            AssertEqual(buildFashion, start.Value<int>("FashionId"), "Theatre6 gameplay start keeps the chosen fashion");
            AssertEqual(buildDifficulty, start.Value<int>("DifficultyId"), "Theatre6 gameplay start keeps the authored difficulty");
            int gameplayStage = start.Value<int>("StageId");
            AssertEqual(RequiemAuthoredStageField(gameplayStage, "HpNum"), start.Value<int>("MaxHealth"),
                "Theatre6 gameplay health derives from the authored stage the difficulty resolved to");
            AssertEqual(RequiemAuthoredStageField(gameplayStage, "BaseSan"), start.Value<int>("MaxSan"),
                "Theatre6 gameplay sanity derives from the authored stage the difficulty resolved to");

            // Reviewed authored signature-skill resolution: the initial buff's family must resolve
            // to this character's own level-1 skill, never the removed Parry fallback.
            int expectedStartSkill = (buildCharacter, buildBuff) switch
            {
                (2, 1) => 10261021,   // Savage Blade, Ignite/Rage family of Theatre6Character 2
                (3, 4) => 10251011,   // Searing Blade, Ignite family of Theatre6Character 3
                (4, 7) => 10271021,   // Radiant Charge, Dawnlight family of Theatre6Character 4
                _ => 0
            };
            if (expectedStartSkill > 0)
                AssertRequiemStartSkill(build, modeKey, buildCharacter, buildBuff, expectedStartSkill);

            build.WalkRun(modeKey, $"gameplay-{buildCharacter}", confirmExtraFloor: confirmExtraFloor);
            AssertEqual(expectGate && confirmExtraFloor, build.ConsumedExtraFloor,
                "Theatre6 gameplay run follows the authored extra-floor entry gate");
            AssertEqual(expectGate && !confirmExtraFloor, build.DeclinedExtraFloor,
                "Theatre6 gameplay run follows the authored extra-floor decline gate");
            JObject settle = build.AcknowledgeRun(modeKey, modeId: 1, slot: build.NextFreeSaveSlot());
            AssertEqual(true, settle.Value<bool>("IsWin"), $"Theatre6 gameplay build {buildCharacter} clears the authored chain");
            Require(build.State.PassDiffRecords.Values.Any(passed => passed > 0),
                "Theatre6 gameplay clear records difficulty progress in durable state");
            AssertEqual(1, build.ArchiveCount(), "Theatre6 gameplay archive lands in the acknowledged slot");
            JObject archived = build.Login()["FileDatas"]!.Children<JObject>()
                .Single(file => file.Value<int>("SlotId") == 1);
            AssertEqual(buildCharacter, archived.Value<int>("CharacterId"), "Theatre6 gameplay archive keeps the built character");
            AssertEqual(buildFashion, archived.Value<int>("FashionId"), "Theatre6 gameplay archive keeps the built fashion");
            Require(archived["Skills"] is JArray, "Theatre6 gameplay archive carries the built skill list.");
        }

        // Authored extra floor: the prompt gate is authored for a ladder that carries an ExFloor floor, and the client
        // only raises it when the run already cleared that difficulty (XTheatre6Control:CheckEnterExFloorConfirm requires
        // WaitingExFloorConfirm && HasClearedBeforeExFloor). The server answers the cleared-before case by replaying the
        // difficulty's alternate stage (StageDifficulty.NewStageIds), which is the variant that carries the extra floor,
        // so the first clear walks the base stage with no gate and the replays exercise accept and decline.
        using (RequiemCase extended = new($"gameplay-{extendedCharacter}-{extendedResolved}-extra-floor"))
        {
            extended.UnlockGameplay();
            RequiemSatisfyDifficultyChain(extended, extendedResolved);

            string firstClearMode = extended.StartGameplayRun(extendedCharacter, extendedFashion, extendedBuff, extendedResolved);
            int firstClearStage = extended.Mode(firstClearMode).Value<int>("StageId");
            AssertEqual(false, RequiemStageHasExtraFloor(firstClearStage),
                "Theatre6 extended tier first clear runs the authored stage without the extra floor");
            extended.WalkRun(firstClearMode, $"extended-{extendedCharacter}-first-clear", confirmExtraFloor: false);
            AssertEqual(false, extended.ConsumedExtraFloor, "Theatre6 extended tier first clear raises no extra-floor gate");
            AssertEqual(false, extended.DeclinedExtraFloor, "Theatre6 extended tier first clear raises no decline");
            JObject firstClearSettle = extended.AcknowledgeRun(firstClearMode, modeId: 1, slot: extended.NextFreeSaveSlot());
            AssertEqual(true, firstClearSettle.Value<bool>("IsWin"), "Theatre6 extended tier first clear wins");
            Require(extended.State.PassStageRecords.ContainsKey(firstClearStage),
                "Theatre6 extended tier first clear records the stage the extra-floor gate is authorised by");

            extended.ResetExtraFloorObserved();
            string acceptedMode = extended.StartGameplayRun(extendedCharacter, extendedFashion, extendedBuff, extendedResolved);
            Require(RequiemStageHasExtraFloor(extended.Mode(acceptedMode).Value<int>("StageId")),
                "Theatre6 cleared-before replay resolves the authored stage that carries the extra floor");
            extended.WalkRun(acceptedMode, $"extended-{extendedCharacter}-extra-accept", confirmExtraFloor: true);
            AssertEqual(true, extended.ConsumedExtraFloor, "Theatre6 cleared-before replay raises the authored extra-floor gate");
            AssertEqual(false, extended.DeclinedExtraFloor, "Theatre6 accepted extra floor is not a decline");
            JObject acceptedSettle = extended.AcknowledgeRun(acceptedMode, modeId: 1, slot: extended.NextFreeSaveSlot());
            AssertEqual(true, acceptedSettle.Value<bool>("IsWin"), "Theatre6 extra-floor acceptance clears the extended tier");

            extended.ResetExtraFloorObserved();
            string declinedMode = extended.StartGameplayRun(extendedCharacter, extendedFashion, extendedBuff, extendedResolved);
            extended.WalkRun(declinedMode, $"extended-{extendedCharacter}-extra-decline", confirmExtraFloor: false);
            AssertEqual(false, extended.ConsumedExtraFloor, "Theatre6 declined extra floor is never entered");
            AssertEqual(true, extended.DeclinedExtraFloor, "Theatre6 cleared-before replay exercises the authored extra-floor decline");
            JObject declinedSettle = extended.AcknowledgeRun(declinedMode, modeId: 1, slot: extended.NextFreeSaveSlot());
            AssertEqual(true, declinedSettle.Value<bool>("IsWin"), "Theatre6 extra-floor decline settles the cleared ladder as a win");
        }
    }

    private static void ValidateRequiemTerminalRecoveryChecks()
    {
        using RequiemCase test = new("terminal-recovery");
        test.UnlockGameplay();
        (int character, int fashion, int buff, int difficulty) = RequiemBuild(0, 0);

        // Abandon is the successful EndGame path: a live run settles as a loss through the same
        // terminal flow the client uses when the player leaves a run.
        string abandoned = test.StartGameplayRun(character, fashion, buff, difficulty);
        test.WalkToRoom(abandoned, 4, "terminal-abandon");
        JObject abandon = test.AbandonRun(abandoned, modeId: 1);
        AssertEqual(false, abandon.Value<bool>("IsWin"), "Theatre6 abandon settles the live run as a loss");
        Require(abandon["FightRecords"] is JArray, "Theatre6 abandon reports the authored fight records.");
        AssertEqual(0, test.State.ActiveRuns.Count, "Theatre6 abandon gives up the run");
        AssertEqual(0, test.State.Settlements.Count, "Theatre6 abandon drops the acknowledged settlement");
        AssertEqual(0, test.ArchiveCount(), "Theatre6 abandon cannot mint an archive");

        // Natural clear: the run records its own settlement, and only the terminal acknowledgement
        // releases it. Relog before that acknowledgement must republish the identical frozen settle.
        string cleared = test.StartGameplayRun(character, fashion, buff, difficulty);
        test.WalkRun(cleared, "terminal-recovery");
        Require(test.SettledMode(cleared), "Theatre6 natural clear records its settlement before acknowledgement.");
        byte[] settled = test.State.ToBson();
        JObject pendingNotify = test.Login();
        JObject? pendingMode = pendingNotify[cleared] as JObject;
        Require(pendingMode?.Value<bool>("IsSettle") == true,
            "Relog before acknowledgement must reopen the pending Theatre6 settlement.");
        Require(pendingMode?["SettleData"] is JObject, "Pending Theatre6 settlement carries frozen SettleData.");
        test.Relog("pending-settlement");
        AssertEqual(Convert.ToHexString(settled), Convert.ToHexString(test.State.ToBson()),
            "Login recovery cannot reroll, repay or drop a frozen Theatre6 settlement");

        int slot = test.NextFreeSaveSlot();
        JObject settle = test.AcknowledgeRun(cleared, modeId: 1, slot: slot);
        AssertEqual(true, settle.Value<bool>("IsWin"), "Theatre6 natural clear settles as a win");
        AssertEqual(1, test.ArchiveCount(), "Theatre6 archive acknowledgement stores exactly one archive");
        JObject archived = test.Login()["FileDatas"]!.Children<JObject>().Single(file => file.Value<int>("SlotId") == slot);
        AssertEqual(character, archived.Value<int>("CharacterId"), "Theatre6 archive keeps the run character identity");
        AssertEqual(fashion, archived.Value<int>("FashionId"), "Theatre6 archive keeps the run fashion identity");
        Require(archived.Value<int>("Score") > 0, "Theatre6 archive acknowledgement carries the settled score.");

        byte[] afterAck = test.State.ToBson();
        test.Call("Theatre6EndGameRequest", Req(("ModeId", 1)), reusePacketId: test.LastSettlePacketId, success: null);
        AssertEqual(Convert.ToHexString(afterAck), Convert.ToHexString(test.State.ToBson()),
            "Acknowledged Theatre6 settlement cannot settle a second time");
        AssertEqual(1, test.ArchiveCount(), "Acknowledged Theatre6 settlement cannot mint a second archive");

        // The archive acknowledgement already released the run, so release state is asserted here and
        // the give-up contract itself is exercised by the abandonment path above.
        byte[] releasedState = test.State.ToBson();
        test.Call("Theatre6GiveUpSaveFileRequest", Req(("ModeId", 1)), success: null);
        AssertEqual(0, test.State.ActiveRuns.Count, "Acknowledged Theatre6 run is released");
        AssertEqual(0, test.State.Settlements.Count, "Acknowledged Theatre6 settlement is dropped");
        AssertEqual(1, test.ArchiveCount(), "Acknowledgement keeps the authored archive");

        test.Relog("acked");
        AssertEqual(1, test.ArchiveCount(), "Acknowledged Theatre6 archive survives a fresh login");
        Require(test.TryLogin() is not null, "Authorized Theatre6 login keeps emitting activity state after acknowledgement.");
    }

    private sealed partial class RequiemCase
    {
        public const string PlayModeKey = "PlayModeDataDb";
        public const string StoryModeKey = "StoryModeDataDb";

        private readonly HashSet<string> solvedFights = [];
        private string? finishedStallKey;
        private int solvedRunIdentity = -1;
        private int choiceOrdinal;
        public int CompletedChoices { get; private set; }
        public bool ConsumedExtraFloor { get; private set; }
        public bool DeclinedExtraFloor { get; private set; }

        // Each extra-floor phase asserts its own observation, so the terminal flags are cleared when a phase starts.
        public void ResetExtraFloorObserved()
        {
            ConsumedExtraFloor = false;
            DeclinedExtraFloor = false;
        }

        public JObject Room(string modeKey) => Mode(modeKey)["CurrentRoomDataDb"] as JObject
            ?? throw new InvalidDataException($"{Name}: {modeKey} has no current room.");

        public string RoomIdentity(string modeKey)
        {
            JObject mode = Mode(modeKey);
            JObject room = Room(modeKey);
            return $"{mode.Value<int>("StageId")}:{mode.Value<int>("CurFloorIdx")}:{room.Value<int>("RoomIdx")}:{room.Value<int>("RoomType")}";
        }

        public int StartStoryRun(int lineIndex, int replayStageId = 0)
        {
            Theatre6StoryLineTable line = RequiemStoryLines()[lineIndex];
            Dictionary<string, object?> request = Req(("StoryLineId", Convert.ToInt32(line.Id)));
            if (replayStageId > 0) request["ReplayStageId"] = replayStageId;
            JObject response = Call("Theatre6EnterStoryLineRequest", request);
            Require(response["StoryModeDataDb"] is JObject, "Theatre6 story entry did not return the story mode snapshot.");
            int stage = response["StoryModeDataDb"]!.Value<int>("StageId");
            Require(stage > 0, "Theatre6 story entry must select an authored stage.");
            if (replayStageId > 0)
                AssertEqual(replayStageId, stage, "Theatre6 story replay must honour the requested replay stage");
            return stage;
        }

        public string StartGameplayRun(int characterId, int fashionId, int initBuffId, int difficultyId)
        {
            JObject response = Call("Theatre6PlayModeStartFightRequest", Req(
                ("CharacterId", characterId), ("FashionId", fashionId),
                ("InitBuffId", initBuffId), ("DifficultyId", difficultyId)));
            Require(response["PlayModeDataDb"] is JObject, "Theatre6 gameplay start did not return the play mode snapshot.");
            return PlayModeKey;
        }

        // Terminal acknowledgement for a run that already recorded its settlement: EndGame may
        // answer AlreadySettle, and Save/GiveUp are the run's release acknowledgements.
        public JObject AcknowledgeRun(string modeKey, int modeId, int slot)
        {
            JObject response = Call("Theatre6EndGameRequest", Req(("ModeId", modeId)), success: null);
            JObject settle = response["SettleData"] as JObject ?? Mode(modeKey)["SettleData"] as JObject
                ?? throw new InvalidDataException("Theatre6 terminal boundary published no settlement.");
            LastSettlePacketId = LastPacketId;
            if (slot > 0)
                Call("Theatre6SaveFileRequest", Req(("ModeId", modeId), ("SlotId", slot)));
            else
                Call("Theatre6GiveUpSaveFileRequest", Req(("ModeId", modeId)));
            return settle;
        }

        // Abandoning a live run is the successful EndGame path: the server settles it as a loss.
        public JObject AbandonRun(string modeKey, int modeId)
        {
            JObject response = Call("Theatre6EndGameRequest", Req(("ModeId", modeId)));
            JObject settle = response["SettleData"] as JObject
                ?? throw new InvalidDataException("Theatre6 abandon did not return a settlement.");
            LastSettlePacketId = LastPacketId;
            Require(Mode(modeKey)["SettleData"] is JObject, "Theatre6 abandon is not published to the client snapshot.");
            Call("Theatre6GiveUpSaveFileRequest", Req(("ModeId", modeId)));
            return settle;
        }

        public int LastSettlePacketId { get; private set; }

        private static List<Theatre6StoryLineTable> RequiemStoryLines() =>
            TableReaderV2.Parse<Theatre6StoryLineTable>().OrderBy(row => Convert.ToInt32(row.Id)).ToList();

        public int ExpectedStoryStartStage(int lineIndex) => ExpectedStoryLineFirstStage(Convert.ToInt32(RequiemStoryLines()[lineIndex].Id));

        public int ExpectedStoryLineFirstStage(int lineId)
        {
            Theatre6StoryLineTable line = RequiemStoryLines().Single(row => Convert.ToInt32(row.Id) == lineId);
            List<int> stageIds = line.StageIds.Where(id => id > 0).ToList();
            if (stageIds.Count > 0) return stageIds[0];
            // Common lines carry no stage list: the authored common stage is the highest "Common Storyline" row.
            Theatre6StageTable common = TableReaderV2.Parse<Theatre6StageTable>()
                .Where(row => row.Name.Contains("Common Storyline", StringComparison.Ordinal))
                .OrderByDescending(row => Convert.ToInt32(row.Id)).First();
            return Convert.ToInt32(common.Id);
        }

        // The client acknowledges a story-detail id (Theatre6StoryDetail.Id) before playing its
        // referenced movie; CommonGuides/CommonFirstGuides are notice-text ids, not wire ids.
        public int CommonGuideStoryId() => Convert.ToInt32(TableReaderV2.Parse<Theatre6StoryDetailTable>()
            .OrderBy(row => Convert.ToInt32(row.Id)).First().Id);

        public int PaidStoryLine() => Convert.ToInt32(RequiemStoryLines()
            .First(row => row.ConsumeCounts.Any(count => count > 0) && row.StageIds.Any(id => id > 0)).Id);

        // Zero-based index of the line's first authored chargeable stage. Every line leaves ConsumeCounts column 1
        // blank and the installed client reads that as "no purchase" (XTool.lua:577 IsNumberValid rejects nil/0,
        // XTheatre6Model:350-352), so stage index 0 is free and this index is the line's first real charge.
        public int PaidStoryStageIndex(int lineId) => RequiemStoryLines()
            .Single(row => Convert.ToInt32(row.Id) == lineId).ConsumeCounts.FindIndex(count => count > 0);

        public int PaidStoryStagePrice(int lineId, int stageIndex)
        {
            List<int> counts = RequiemStoryLines().Single(row => Convert.ToInt32(row.Id) == lineId).ConsumeCounts;
            return stageIndex >= 0 && stageIndex < counts.Count ? counts[stageIndex] : 0;
        }

        // The purchase record the client reads back: zero-based stage indexes in BuyIndex
        // (XTheatre6Model:IsStoryLineStageHasBuy compares table.contains(data.BuyIndex, stageIndex - 1)).
        public List<int> BoughtStageIndexes(int lineId) =>
            ((JArray?)Login()["StoryLineDatas"]!.Children<JObject>()
                .Single(line => line.Value<int>("StoryLineId") == lineId)["BuyIndex"])?.Values<int>().ToList()
            ?? new List<int>();

        public int StoryLineIndex(int lineId) => RequiemStoryLines().FindIndex(row => Convert.ToInt32(row.Id) == lineId);

        public int StoryLineCoin() => RequiemConfigValue("Theatre6Coin");

        // One authored chain step. Returns true when the run has settled.
        public bool StepRun(string modeKey, string label, bool confirmExtraFloor = true)
        {
            JObject mode = Mode(modeKey);
            if (mode.Value<bool>("IsSettle"))
                return true;
            if (mode.Value<bool>("WaitingExFloorConfirm"))
            {
                if (confirmExtraFloor)
                    ConfirmExtraFloor(modeKey, label);
                else
                    DeclineExtraFloor(modeKey, label);
                return !confirmExtraFloor;
            }

            // The unresolved overflow queue blocks every advancement (choice, task, room action),
            // not only leaving the shop, so it is cleared first wherever it appears.
            if (ResolveOverflowQueue(modeKey)) return false;

            JObject room = Room(modeKey);
            int roomType = room.Value<int>("RoomType");
            if (roomType is 4 or 5 && room.Value<int>("SelectedMonsterId") == 0)
            {
                Call("Theatre6FightRoomSlideRequest", Req(("SelectType", RequestedMonsterSide(room))));
                Require(Room(modeKey).Value<int>("SelectedMonsterId") != 0,
                    $"{label}: Theatre6 monster selection did not freeze SelectedMonsterId.");
                return false;
            }

            // The authored fight ids repeat across runs (a replay reuses 9999/500), so the solved set
            // is scoped by the persisted run identity, never by a wire field.
            int runModeId = modeKey == PlayModeKey ? 1 : 2;
            int runIdentity = State.ActiveRuns.TryGetValue(runModeId, out var activeRun) ? activeRun.RunId : 0;
            // The same authored fight id can recur inside one run and one room as the choice cursor
            // advances, so the cursor identity is part of the key and a fresh repeat is still solved.
            string fightKey = $"{runIdentity}:{modeKey}:{mode.Value<int>("CurFloorIdx")}:{room.Value<int>("RoomIdx")}:" +
                $"{room.Value<int>("FightId")}:{room.Value<int>("SelectedMonsterId")}:" +
                $"{room.Value<int?>("CurChoosePoolIdx") ?? -1}:{room.Value<int?>("CurChooseId") ?? -1}";
            if (room.Value<int>("SelectedMonsterId") != 0 && solvedFights.Add(fightKey))
            {
                SolveFight(modeKey, fightKey, label, win: true);
                return false;
            }

            switch (roomType)
            {
                case 6:
                    Call("Theatre6EndAvgRoomRequest", null);
                    return false;
                case 3:
                    ShopVisit(modeKey, label);
                    return false;
                case 1:
                case 2:
                    RoomProgress(modeKey, room, label);
                    return false;
                case 4:
                case 5:
                    // The authored monster/boss fight of this room is already resolved; the server
                    // advances the chain and a repeat of the identical room is a real stall.
                    return AwaitChainAdvance(modeKey, room, label);
                default:
                    throw new InvalidDataException($"{label}: Theatre6 room {room.Value<int>("RoomIdx")} has unhandled type {roomType}.");
            }
        }

        // Clears a non-empty overflow queue through the registered sale and asserts the authored
        // payout; returns true when it resolved one, so the caller re-reads a fresh snapshot.
        private bool ResolveOverflowQueue(string modeKey)
        {
            List<int> queue = (Mode(modeKey)["SkillOverQueue"] as JArray ?? []).Values<int>().ToList();
            if (queue.Count == 0)
                return false;
            int expected = queue.Sum(queued => Convert.ToInt32(TableReaderV2.Parse<Theatre6SkillTable>()
                .Single(row => Convert.ToInt32(row.Id) == queued).SellPrice));
            int goldBefore = Gold(modeKey);
            Call("Theatre6SkillOverQueueSellRequest", null);
            AssertEqual(0, (Mode(modeKey)["SkillOverQueue"] as JArray)?.Count ?? 0,
                "Theatre6 overflow sale consumes the whole queue wherever it appears");
            AssertEqual(goldBefore + expected, Gold(modeKey), "Theatre6 overflow sale pays the authored skill value");
            return true;
        }

        // A room whose fight is resolved waits for the server's chain advance; only an identical
        // repeat without any progress is a stall.
        private bool AwaitChainAdvance(string modeKey, JObject room, string label)
        {
            if (Mode(modeKey).Value<bool>("IsSettle"))
                return true;
            int runModeId = modeKey == PlayModeKey ? 1 : 2;
            int identity = State.ActiveRuns.TryGetValue(runModeId, out var activeRun) ? activeRun.RunId : 0;
            if (identity != solvedRunIdentity)
            {
                // A new run reuses authored fight ids, so the rescue/stall memory is cleared per run.
                solvedRunIdentity = identity;
                finishedStallKey = null;
            }

            string key = RoomIdentity(modeKey);
            if (finishedStallKey == key)
                throw new InvalidDataException(
                    $"{label}: Theatre6 room {room.Value<int>("RoomIdx")} (type {room.Value<int>("RoomType")}) " +
                    $"stayed resolved without advancing ({RequiemRoomTrace(room, Pushes)}).");
            finishedStallKey = key;
            return false;
        }

        public void WalkRun(string modeKey, string label, bool confirmExtraFloor = true)
        {
            for (int step = 0; step < 512; step++)
            {
                if (StepRun(modeKey, label, confirmExtraFloor))
                    return;
            }

            throw new InvalidDataException($"{label}: Theatre6 run did not settle within the authored step budget.");
        }

        public void WalkToRoom(string modeKey, int roomType, string label)
        {
            for (int step = 0; step < 512; step++)
            {
                if (Mode(modeKey).Value<bool>("IsSettle"))
                    break;
                if (Room(modeKey).Value<int>("RoomType") == roomType)
                    return;
                StepRun(modeKey, label);
            }

            throw new InvalidDataException($"{label}: Theatre6 run never reached authored room type {roomType}.");
        }

        private void RoomProgress(string modeKey, JObject room, string label)
        {
            int status = room.Value<int>("ChooseRoomStatus");
            if (status is 1)
            {
                ConfirmTasks(modeKey, label);
                return;
            }

            if (status is 2)
            {
                JObject response = Call("Theatre6ChooseEventRequest", Req(("SelectType", RequestedChoiceSide(room))));
                CompletedChoices++;
                Require(response["NextRoomStatus"] is not null || response["NextChooseId"] is not null,
                    $"{label}: Theatre6 choice response carries neither NextRoomStatus nor NextChooseId.");
                // A choice may spawn a fight (RewardType 7 with FightId/MonsterId) while the room stays
                // in ChooseEvent: the client must clear that native fight before the next choice, so
                // the harness enters and settles it here instead of walking into the pending-fight guard.
                JObject? spawned = (response["RewardGoodsList"] as JArray ?? []).Children<JObject>()
                    .FirstOrDefault(reward => Convert.ToInt32(reward["FightId"]?.Value<int>() ?? 0) > 0
                        || Convert.ToInt32(reward["MonsterId"]?.Value<int>() ?? 0) > 0);
                if (spawned is not null)
                {
                    string spawnedKey = $"{modeKey}:choice:{response.Value<int?>("NextChooseId") ?? 0}:" +
                        $"{spawned["FightId"]?.Value<int>() ?? 0}:{spawned["MonsterId"]?.Value<int>() ?? 0}";
                    if (solvedFights.Add(spawnedKey))
                        SolveFight(modeKey, spawnedKey, label, win: true);
                }

                if (response.Value<int?>("NextRoomStatus") is 3)
                    Call("Theatre6RecvTaskRoomRewardRequest", null);
                return;
            }

            if (status is 3)
            {
                Call("Theatre6RecvTaskRoomRewardRequest", null);
                return;
            }

            if (status is 4 or 5)
            {
                // ChooseRoomFinish releases the final task reward; Finished waits for the server's
                // next-room/floor push. A settled choice/event battle legitimately leaves the room
                // here with no pending fight, so a missing fight is not by itself a stall.
                if (room.Value<int>("SelectedMonsterId") != 0)
                    return;
                if (status is 4)
                {
                    Call("Theatre6RecvTaskRoomRewardRequest", null);
                    return;
                }

                string finishedKey = RoomIdentity(modeKey);
                if (finishedStallKey == finishedKey)
                    throw new InvalidDataException(
                        $"{label}: Theatre6 room {room.Value<int>("RoomIdx")} stayed at ChooseRoomStatus 5 " +
                        $"without advancing ({RequiemRoomTrace(room, Pushes)}).");
                finishedStallKey = finishedKey;
                return;
            }

            throw new InvalidDataException($"{label}: Theatre6 room status {status} cannot progress.");
        }

        private void ConfirmTasks(string modeKey, string label)
        {
            // The authored task group travels on the mode snapshot (not on the room), and the
            // client confirms exactly the authored ChooseNum of the tasks the server offered.
            int taskGroupId = Mode(modeKey).Value<int>("TaskGroupId");
            Theatre6StageTaskGroupTable? group = taskGroupId != 0
                ? TableReaderV2.Parse<Theatre6StageTaskGroupTable>().FirstOrDefault(row => Convert.ToInt32(row.Id) == taskGroupId)
                : null;
            // Only the authored task group caps the selection; a room without one confirms exactly
            // the tasks the server offered in its slots.
            int chooseNum = group is null ? 0 : Math.Max(1, Convert.ToInt32(group.ChooseNum));
            // The authored refresh budget is MaxRefresh (an empty budget forbids refreshing), and the
            // slot must not have consumed it yet; FreeRefresh alone is not a sufficient gate.
            if (Mode(modeKey)["TaskSlotData"] is JArray { Count: > 0 } slots
                && slots[0] is JObject slot
                && group is not null && Convert.ToInt32(group.MaxRefresh) > 0
                && slot.Value<int>("RefreshCount") < Convert.ToInt32(group.MaxRefresh))
            {
                JObject refreshed = Call("Theatre6RefreshTaskRequest", Req(("Index", 1)));
                Require(refreshed["NewTask"] is JObject, "Theatre6 task refresh must offer a replacement task.");
            }

            // The offered tasks are the ones the server placed in the slots; the authored choose
            // limit only caps how many of those may be confirmed.
            List<int> offered = (Mode(modeKey)["TaskSlotData"] as JArray ?? []).Children<JObject>()
                .Select(slot => slot.Value<int?>("TaskId") ?? 0).Where(id => id > 0).Distinct().ToList();
            List<int> selected = chooseNum > 0 && offered.Count > chooseNum ? offered.Take(chooseNum).ToList() : offered;
            if (selected.Count == 0)
                throw new InvalidDataException($"{label}: Theatre6 task room offered no authored tasks.");
            JObject response = Call("Theatre6ConfirmTaskRequest", Req(("TaskIds", selected)));
            if (response.Value<int?>("NextRoomStatus") is 3)
                Call("Theatre6RecvTaskRoomRewardRequest", null);
        }

        private void ConfirmExtraFloor(string modeKey, string label)
        {
            AssertEqual(true, Mode(modeKey).Value<bool>("HasClearedBeforeExFloor"),
                $"{label}: Theatre6 extra-floor gate follows an already-cleared extra floor.");
            JObject response = Call("Theatre6ExFloorConfirmRequest", Req(("IsEnter", true)));
            Require(response["ModeDataDb"] is JObject, $"{label}: accepted Theatre6 extra floor must return ModeDataDb.");
            Require(Pushes.Any(push => push.Name == "NotifyTheatre6NewFloorData"),
                $"{label}: accepted Theatre6 extra floor must publish NotifyTheatre6NewFloorData.");
            ConsumedExtraFloor = true;
        }

        private void DeclineExtraFloor(string modeKey, string label)
        {
            JObject response = Call("Theatre6ExFloorConfirmRequest", Req(("IsEnter", false)));
            Require(response["SettleData"] is JObject, $"{label}: declined Theatre6 extra floor must return the settlement.");
            Require(response["StoryModeSaveDb"] is JObject, $"{label}: declined Theatre6 extra floor must return the story save.");
            DeclinedExtraFloor = true;
        }

        private int RequestedChoiceSide(JObject room)
        {
            // Prefer the authored side that spawns a fight so the choice-fight branch is exercised;
            // otherwise alternate deterministically so both authored sides are consumed.
            foreach (int side in new[] { 1, 2 })
            {
                string key = side == 1 ? "LeftRewards" : "RightRewards";
                if (room[key] is JArray rewards
                    && rewards.Children<JObject>().Any(reward => Convert.ToInt32(reward["FightId"]?.Value<int>() ?? 0) > 0))
                {
                    return side;
                }
            }

            return choiceOrdinal++ % 2 == 0 ? 1 : 2;
        }

        private static int RequestedMonsterSide(JObject room) => room["FightRewards"] is JArray { Count: > 0 } ? 1 : 2;
    }

    private static (int CharacterId, int FashionId, int InitBuffId, int DifficultyId) RequiemBuild(int characterIndex, int difficultyIndex)
    {
        List<Theatre6CharacterTable> characters = TableReaderV2.Parse<Theatre6CharacterTable>()
            .Where(row => Convert.ToInt32(row.FashionIds) > 0 && Convert.ToInt32(row.PlayDiffGroupIds) > 0 && row.BaseSkill.Count > 0)
            .OrderBy(row => Convert.ToInt32(row.Id)).ToList();
        Theatre6CharacterTable character = characters[characterIndex % characters.Count];
        Theatre6CharacterFashionTable fashion = TableReaderV2.Parse<Theatre6CharacterFashionTable>()
            .Where(row => Convert.ToInt32(row.Id) == Convert.ToInt32(character.FashionIds))
            .OrderBy(row => Convert.ToInt32(row.Id)).First();
        Theatre6StageDifficultyGroupTable group = TableReaderV2.Parse<Theatre6StageDifficultyGroupTable>()
            .Single(row => Convert.ToInt32(row.Id) == Convert.ToInt32(character.PlayDiffGroupIds));
        // Prefer difficulties without an authored unlock condition so both builds start from
        // table-derived state; gated rows are reached through their authored condition chain.
        List<Theatre6StageDifficultyTable> difficulties = TableReaderV2.Parse<Theatre6StageDifficultyTable>()
            .Where(row => group.DifficultyIds.Contains(Convert.ToInt32(row.Id)))
            .Where(row => Convert.ToInt32(row.ConditionId ?? 0) == 0)
            .OrderBy(row => Convert.ToInt32(row.Id)).ToList();
        if (difficulties.Count == 0)
            throw new InvalidDataException($"Theatre6 difficulty group {group.Id} has no unconditioned difficulty row.");
        Theatre6StageDifficultyTable difficulty = difficulties[difficultyIndex % difficulties.Count];
        int initBuff = character.TagBuffIds.Count > 0 ? character.TagBuffIds[0] : 0;
        return (Convert.ToInt32(character.Id), Convert.ToInt32(fashion.Id), initBuff, Convert.ToInt32(difficulty.Id));
    }

    private static void AssertRequiemStartSkill(RequiemCase test, string modeKey, int characterId, int initBuffId, int expectedSkillId)
    {
        Theatre6SkillTable expected = TableReaderV2.Parse<Theatre6SkillTable>()
            .Single(row => Convert.ToInt32(row.Id) == expectedSkillId);
        AssertEqual(characterId, Convert.ToInt32(expected.Character),
            $"Theatre6 start skill {expectedSkillId} belongs to the started character");
        AssertEqual(1, Convert.ToInt32(expected.Level), $"Theatre6 start skill {expectedSkillId} is an authored level-1 skill");
        Require((test.Mode(modeKey)["Skills"] as JArray ?? []).Children<JObject>()
                .Any(skill => skill.Value<int>("SkillId") == expectedSkillId),
            $"Theatre6 gameplay start with initial buff {initBuffId} must grant the authored signature skill {expectedSkillId}.");
    }

    // Room/status/fight trace plus the pushes seen, used when a finished room refuses to advance.
    private static string RequiemRoomTrace(JObject room, IEnumerable<(string Name, JObject Body)> pushes) =>
        $"RoomIdx={room.Value<int?>("RoomIdx")}, RoomType={room.Value<int?>("RoomType")}, " +
        $"ChooseRoomStatus={room.Value<int?>("ChooseRoomStatus")}, FightId={room.Value<int?>("FightId")}, " +
        $"SelectedMonsterId={room.Value<int?>("SelectedMonsterId")}, " +
        $"pushes=[{string.Join(", ", pushes.Select(push => push.Name))}]";

    // Authored stage row lookup by STAGE id (the difficulty lookup belongs to RequiemDifficultyStage).
    private static int RequiemAuthoredStageField(int stageId, string field)
    {
        Theatre6StageTable stage = TableReaderV2.Parse<Theatre6StageTable>()
            .Single(row => Convert.ToInt32(row.Id) == stageId);
        return field switch
        {
            "HpNum" => Convert.ToInt32(stage.HpNum),
            "BaseSan" => Convert.ToInt32(stage.BaseSan),
            _ => throw new InvalidDataException($"Unknown Theatre6 stage field {field}.")
        };
    }

    private static int RequiemDifficultyStage(int difficultyId)
    {
        Theatre6StageDifficultyTable difficulty = TableReaderV2.Parse<Theatre6StageDifficultyTable>()
            .Single(row => Convert.ToInt32(row.Id) == difficultyId);
        if (Convert.ToInt32(difficulty.StageId ?? 0) > 0)
            return Convert.ToInt32(difficulty.StageId);
        int newStage = Convert.ToInt32(difficulty.NewStageIds ?? 0);
        if (newStage > 0) return newStage;
        throw new InvalidDataException($"Theatre6 difficulty {difficultyId} resolves no authored stage.");
    }

    // Authored extra floors live on the floor rows of the stage a run chains into; the gate must
    // appear exactly for those ladders and never for zones without one.
    private static bool RequiemAuthoredExtraFloor(int difficultyId)
    {
        Theatre6StageDifficultyTable difficulty = TableReaderV2.Parse<Theatre6StageDifficultyTable>()
            .Single(row => Convert.ToInt32(row.Id) == difficultyId);
        List<int> stages = [];
        int newStage = Convert.ToInt32(difficulty.NewStageIds ?? 0);
        if (newStage > 0) stages.Add(newStage);
        if (Convert.ToInt32(difficulty.StageId ?? 0) > 0)
            stages.Add(Convert.ToInt32(difficulty.StageId));
        if (stages.Count == 0)
            stages.Add(RequiemDifficultyStage(difficultyId));
        return stages.Any(RequiemStageHasExtraFloor);
    }

    private static bool RequiemStageHasExtraFloor(int stageId)
    {
        Dictionary<int, Theatre6StageFloorTable> floors = TableReaderV2.Parse<Theatre6StageFloorTable>().ToDictionary(row => Convert.ToInt32(row.Id));
        Theatre6StageTable stage = TableReaderV2.Parse<Theatre6StageTable>().Single(row => Convert.ToInt32(row.Id) == stageId);
        return stage.FloorIds.Where(id => id > 0).Any(id => floors.TryGetValue(id, out Theatre6StageFloorTable? floor)
            && Convert.ToInt32(floor.ExFloor ?? 0) > 0);
    }

    // The authored extended tier is not listed in any character difficulty group; its own unlock
    // condition names the prerequisite difficulty, which identifies the character ladder it extends.
    private static int RequiemExtendedDifficulty()
    {
        List<Theatre6StageDifficultyTable> difficulties = TableReaderV2.Parse<Theatre6StageDifficultyTable>()
            .Where(row => RequiemAuthoredExtraFloor(Convert.ToInt32(row.Id)))
            .OrderBy(row => Convert.ToInt32(row.Id)).ToList();
        if (difficulties.Count == 0)
            throw new InvalidDataException("Theatre6 tables author no extended difficulty with an extra floor.");
        return Convert.ToInt32(difficulties[0].Id);
    }

    private static (int CharacterId, int FashionId, int InitBuffId, int DifficultyId) RequiemExtendedBuild(int difficultyId)
    {
        Theatre6StageDifficultyTable difficulty = TableReaderV2.Parse<Theatre6StageDifficultyTable>()
            .Single(row => Convert.ToInt32(row.Id) == difficultyId);
        ConditionTable? condition = difficulty.ConditionId is { } conditionId && conditionId > 0
            ? TableReaderV2.Parse<ConditionTable>().FirstOrDefault(row => Convert.ToInt32(row.Id) == conditionId)
            : null;
        int prerequisite = condition is not null && Convert.ToInt32(condition.Type) == 23202 && condition.Params.Count > 0
            ? Convert.ToInt32(condition.Params[0])
            : 0;
        List<Theatre6CharacterTable> characters = TableReaderV2.Parse<Theatre6CharacterTable>()
            .Where(row => Convert.ToInt32(row.FashionIds) > 0 && Convert.ToInt32(row.PlayDiffGroupIds) > 0 && row.BaseSkill.Count > 0)
            .OrderBy(row => Convert.ToInt32(row.Id)).ToList();
        Theatre6CharacterTable character = characters.FirstOrDefault(row =>
                TableReaderV2.Parse<Theatre6StageDifficultyGroupTable>()
                    .Single(group => Convert.ToInt32(group.Id) == Convert.ToInt32(row.PlayDiffGroupIds))
                    .DifficultyIds.Contains(difficultyId))
            ?? characters.FirstOrDefault(row => prerequisite > 0 && TableReaderV2.Parse<Theatre6StageDifficultyGroupTable>()
                .Single(group => Convert.ToInt32(group.Id) == Convert.ToInt32(row.PlayDiffGroupIds))
                .DifficultyIds.Contains(prerequisite))
            ?? characters[0];
        Theatre6CharacterFashionTable fashion = TableReaderV2.Parse<Theatre6CharacterFashionTable>()
            .Where(row => Convert.ToInt32(row.Id) == Convert.ToInt32(character.FashionIds))
            .OrderBy(row => Convert.ToInt32(row.Id)).First();
        return (Convert.ToInt32(character.Id), Convert.ToInt32(fashion.Id),
            character.TagBuffIds.Count > 0 ? character.TagBuffIds[0] : 0, difficultyId);
    }

    // The authored unlock chain the tables express for a difficulty (condition type 23202 over the
    // durable difficulty records), applied as the same state a cleared ladder produces.
    private static void RequiemSatisfyDifficultyChain(RequiemCase test, int difficultyId)
    {
        Theatre6StageDifficultyTable difficulty = TableReaderV2.Parse<Theatre6StageDifficultyTable>()
            .Single(row => Convert.ToInt32(row.Id) == difficultyId);
        if (difficulty.ConditionId is not { } conditionId || conditionId <= 0)
            return;
        ConditionTable condition = TableReaderV2.Parse<ConditionTable>().Single(row => Convert.ToInt32(row.Id) == conditionId);
        Require(Convert.ToInt32(condition.Type) == 23202 && condition.Params.Count >= 2,
            $"Theatre6 difficulty {difficultyId} unlock condition is not the authored difficulty-progress condition.");
        int prerequisite = Convert.ToInt32(condition.Params[0]);
        test.State.PassDiffRecords[prerequisite] = Math.Max(
            test.State.PassDiffRecords.GetValueOrDefault(prerequisite), Convert.ToInt32(condition.Params[1]));
        test.SaveFixture();
    }
}
