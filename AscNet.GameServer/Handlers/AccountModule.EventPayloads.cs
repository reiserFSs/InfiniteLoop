using AscNet.Common.MsgPack;

namespace AscNet.GameServer.Handlers
{
    internal partial class AccountModule
    {
        private static NotifyAccumulatedPayData BuildCurrentAccumulatedPayData()
        {
            return new()
            {
                PayId = 1,
                PayMoney = 0f,
                PayRewardIds = [],
                ExtraPayRewardIds = []
            };
        }

        private static Dictionary<string, object?> BuildNewActivityCalendarPayload() => PayloadFromJson("""{"OpenActivityIds":[45001,45002,45003,45004,45005,45007,45009,45010],"NewActivityCalendarData":{"TimeLimitActivityInfos":[{"ActivityId":45010,"PeriodInfos":[{"PeriodId":4501001,"GotRewards":[]}]},{"ActivityId":45009,"PeriodInfos":[{"PeriodId":4500901,"GotRewards":[]}]},{"ActivityId":45005,"PeriodInfos":[{"PeriodId":4500501,"GotRewards":[]}]},{"ActivityId":45001,"PeriodInfos":[{"PeriodId":4500101,"GotRewards":[]}]},{"ActivityId":45007,"PeriodInfos":[{"PeriodId":4500701,"GotRewards":[]}]}],"WeekActivityInfos":[{"MainId":1002,"SubId":1,"GotRewards":[]},{"MainId":1001,"SubId":4,"GotRewards":[]},{"MainId":1005,"SubId":1,"GotRewards":[]},{"MainId":1003,"SubId":2,"GotRewards":[]},{"MainId":1004,"SubId":2,"GotRewards":[]}]},"CurrentGuildBossEndTime":1783918800}""");
        private static Dictionary<string, object?> BuildWheelchairManualActivityPayload() => PayloadFromJson("""{"OpenActivityIds":[10001,10002,10003,10004,10005,10006,10007,10008,10009,10010,10011,10012],"CurrentGuildBossEndTime":1783918800,"ActivityId":1,"PlanId":1007,"BpLevel":1,"IsSeniorManualUnlock":false,"StartTick":1747235356,"GetRewardManualRewardIds":[],"GetRewardPlanIds":[],"FinishStageIds":[],"TimeLimitActivityInfos":[],"WeekActivityInfos":[],"BluePointSet":[],"RedPointSet":[]}""");
        private static Dictionary<string, object?> BuildWheelchairManualActivityUpdatePayload() => PayloadFromJson("""{"UpdateTimeLimitActivityInfos":[],"UpdateWeekActivityInfos":null,"CurrentGuildBossEndTime":0}""");
        private static Dictionary<string, object?> BuildAccumulateExpendPayload() => PayloadFromJson("""{"ActivityId":7}""");
        private static Dictionary<string, object?> BuildTurntablePayload() => PayloadFromJson("""{"TurntableData":{"ActivityId":4,"AccumulateDrawNum":0,"GainRewardInfos":[],"GainRecords":[],"GainAccumulateRewardIndexs":[]}}""");
        private static Dictionary<string, object?> BuildFestivalPayload() => PayloadFromJson("""{"FestivalInfos":[{"Id":24,"StageInfos":[{"Id":30130507,"ChallengeCount":0},{"Id":30130508,"ChallengeCount":0},{"Id":30130512,"ChallengeCount":0},{"Id":30130510,"ChallengeCount":0},{"Id":30130511,"ChallengeCount":0},{"Id":30130513,"ChallengeCount":0},{"Id":30130509,"ChallengeCount":0},{"Id":30130514,"ChallengeCount":0}],"FubenEventInfos":null},{"Id":29,"StageInfos":[{"Id":30131155,"ChallengeCount":0},{"Id":30131150,"ChallengeCount":0},{"Id":30131151,"ChallengeCount":0},{"Id":30131152,"ChallengeCount":0},{"Id":30131156,"ChallengeCount":0},{"Id":30131157,"ChallengeCount":0},{"Id":30131153,"ChallengeCount":0},{"Id":30131158,"ChallengeCount":0},{"Id":30131154,"ChallengeCount":0},{"Id":30131159,"ChallengeCount":0}],"FubenEventInfos":null},{"Id":23,"StageInfos":[{"Id":30131113,"ChallengeCount":0},{"Id":30131114,"ChallengeCount":0},{"Id":30131115,"ChallengeCount":0}],"FubenEventInfos":null},{"Id":25,"StageInfos":[{"Id":30131124,"ChallengeCount":0},{"Id":30131125,"ChallengeCount":0}],"FubenEventInfos":null},{"Id":30,"StageInfos":[{"Id":30130310,"ChallengeCount":0},{"Id":30130311,"ChallengeCount":0},{"Id":30130312,"ChallengeCount":0},{"Id":30130313,"ChallengeCount":0},{"Id":30130314,"ChallengeCount":0},{"Id":30130315,"ChallengeCount":0},{"Id":30130316,"ChallengeCount":0},{"Id":30130317,"ChallengeCount":0},{"Id":30130318,"ChallengeCount":0},{"Id":30130319,"ChallengeCount":0}],"FubenEventInfos":null},{"Id":27,"StageInfos":[{"Id":30130212,"ChallengeCount":0},{"Id":30130213,"ChallengeCount":0}],"FubenEventInfos":null}]}""");
        private static Dictionary<string, object?> BuildGame2048Payload() => PayloadFromJson("""{"Game2048DataDb":{"ActivityId":4,"StageContext":null,"StageFinish":[]}}""");
        private static Dictionary<string, object?> BuildGameCollectionPayload() => PayloadFromJson("""{"GameCollectionData":{"ActivityId":1,"GameData":{}}}""");
        private static Dictionary<string, object?> BuildGoldenMinerPayload() => PayloadFromJson("""{"StageDataDb":{"ActivityId":7,"StageScores":0,"TotalMaxScores":0,"TotalMaxScoresCharacter":0,"TotalMaxScoresHexes":null,"TodayPlayGame":0,"TotalPlayCount":0,"CurrentPlayStage":0,"CurrentState":0,"RedEnvelopeProgress":{},"CharacterId":0,"CharacterDbs":[],"FinishStageId":[],"ItemColumns":{},"BuffColumns":{},"UpgradeStrengthens":[],"MinerShopDbs":[],"ItemBuyRecord":{},"StageMapInfos":[{"StageId":1,"MapId":72215},{"StageId":2,"MapId":72814},{"StageId":3,"MapId":72810},{"StageId":4,"MapId":72821},{"StageId":5,"MapId":72830},{"StageId":6,"MapId":72840},{"StageId":7,"MapId":72852},{"StageId":8,"MapId":72860}],"HideTaskInfo":[],"HideStageCount":0,"TotalScore":0,"IsSaveFailed":false,"HexRecords":[],"HexUpgradeRecord":{},"HexHistory":[],"TotalHexCount":0,"FinishTeachMap":[],"IsFinishAllTeach":false,"RandMapIds":[72813,72814,72810,72821,72830,72840,72852,72860,72215],"CoreGenerateResults":[],"CommonGenerateResults":[],"CommonHexSelectCount":0,"CommonHexRefreshCount":0}}""");
        private static Dictionary<string, object?> BuildTaikoMasterPayload() => PayloadFromJson("""{"TaikoMasterData":{"ActivityId":0,"StageDataList":[],"Setting":{"AppearOffset":0,"JudgeOffset":0}}}""");
        private static Dictionary<string, object?> BuildActivityDrawListPayload() => PayloadFromJson("""{"DrawIdList":[1488,1498,2482,2492,4001,4003,4005,4007,4009,4011,4013]}""");
        private static Dictionary<string, object?> BuildActivityDrawGroupCountPayload() => PayloadFromJson("""{"Count":3}""");
        private static Dictionary<string, object?> BuildSelfChoiceLottoPayload() => PayloadFromJson("""{"LottoPrimaryIds":[2],"SelectedPrimaryIdToLottoId":{}}""");

        private static readonly int[] CurrentEventTaskBatchTheatre5 = [140107, 140108, 140109, 140110];
        private static readonly int[] CurrentEventTaskBatchBounty = [15001, 15002];
        private static readonly int[] CurrentEventTaskBatchArena = [3863, 3864, 3865, 3866, 3900, 3901, 3902, 3903];
        private static readonly int[] RetroArcadeTaskBatchEntry = [78020, 78021, 78022, 78023, 78024, 78025, 78026, 78027, 78028, 78029, 78030, 97625, 97626, 97627, 97628, 97629, 97630, 97631, 97632, 97633, 97634, 97635, 97645, 97646, 90933, 99928, 99929, 99930];
        private static readonly int[] RetroArcadeTaskBatchPostTaikoA = [86513, 86514, 86515, 86516];
        private static readonly int[] RetroArcadeTaskBatchPostTaikoB = [85951, 85965, 86023, 86024, 86025, 86026, 86027, 86028, 86029, 86030, 86031, 86032, 86033, 86034, 86035, 86036, 86037, 86038, 86052, 86053, 86054, 86055, 86056, 86057, 86058, 86059, 86060, 86061, 86062, 86063, 86064, 86065, 86066, 86078, 86079, 86080, 86092, 86093, 86094, 86214, 86228, 86326, 86340];
        private static readonly int[] RetroArcadeTaskBatchPostSubModesA = [990000, 990001, 990002, 990003, 990004, 990005, 990006, 990011, 990012, 990013, 990014, 990015, 990016, 990017, 990018, 990019, 990020, 990021, 990022, 990023, 990024, 990025, 990026, 990027, 990028, 990029, 990030, 990031, 990032, 990033, 990034, 990035, 990036, 990037, 990046, 990047, 990048, 990049];
        private static readonly int[] RetroArcadeTaskBatchPostSubModesB = [990050, 990051, 990052, 990053, 990054, 990055, 990056, 990057, 990058, 990059, 990060, 990061, 990062, 990063, 990064, 990065, 990066, 990067, 990068, 990069, 990070, 990071, 990072, 990073, 990074, 990075, 990076, 990077, 990078, 990079, 990080, 990081, 990082, 990083, 990084];
        private static readonly int[] RetroArcadeTaskBatchPostSubModesC = [990101, 990102, 990103, 990104, 990105, 990106, 990107, 990108, 990109, 990110, 990111, 990112, 990113, 990114, 990115, 990116, 990117, 990118, 990119];

        private static void SendCurrentEventTaskBatch(Session session, int[] taskIds)
        {
            TaskModule.SendCurrentTaskBatch(session, taskIds);
        }

        private static Dictionary<string, object?> PayloadFromJson(string json)
        {
            return MessagePackPayloads.PayloadFromJson(json);
        }
    }
}
