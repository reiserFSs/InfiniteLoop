using AscNet.Common.Database;
using AscNet.Common.Util;
using AscNet.Common.MsgPack;
using AscNet.Table.V2.share.fuben;
using AscNet.Table.V2.share.movie;
using MessagePack;

namespace AscNet.GameServer.Handlers
{
    #region MsgPackScheme
#pragma warning disable CS8618
    [MessagePackObject(true)]
    public class StartAutoFightRequest
    {
        public int StageId { get; set; }
        public int Times { get; set; }
    }

    [MessagePackObject(true)]
    public class AutoFightRecordDto
    {
        public int StageId { get; set; }
        public List<long> CardIds { get; set; } = [];
        public long CompleteTime { get; set; }
    }

    [MessagePackObject(true)]
    public class StartAutoFightResponse
    {
        public int Code { get; set; }
        public AutoFightRecordDto? Record { get; set; }
    }

    [MessagePackObject(true)]
    public class ObtainAutoFightRewardsRequest
    {
        public int Index { get; set; }
        public int StageId { get; set; }
    }

    [MessagePackObject(true)]
    public class ObtainAutoFightRewardsResponse
    {
        public int Code { get; set; }
        public int TeamExp { get; set; }
        public int CharacterExp { get; set; }
        public List<RewardGoods> Rewards { get; set; } = [];
    }

    [MessagePackObject(true)]
    public class QueryMovieOptionsRequest
    {
        public string MovieId { get; set; }
    }

    [MessagePackObject(true)]
    public class QueryMovieOptionsResponse
    {
        public int Code { get; set; }
        public List<int> MovieList { get; set; } = [];
    }

    [MessagePackObject(true)]
    public class UpdateMovieOptionsRequest
    {
        public string MovieId { get; set; }
        public int OptionId { get; set; }
    }

    [MessagePackObject(true)]
    public class UpdateMovieOptionsResponse
    {
        public int Code { get; set; }
    }
#pragma warning restore CS8618
    #endregion

    internal class MiscPlayerModule
    {
        internal static Func<DateTimeOffset> Clock = () => DateTimeOffset.UtcNow;

        // XCode FubenManagerAutoFight* / MovieOption* / CdKey* (CodeText.tsv)
        internal const int AutoFightStageInvalid = 20003103, AutoFightHaventPassed = 20003104, AutoFightAlreadyRunning = 20003106,
            AutoFightNotEnoughTimes = 20003107, AutoFightNotEnoughActionPoint = 20003108, AutoFightIndexInvalid = 20003109,
            AutoFightStageNotMatch = 20003110, AutoFightNotComplete = 20003111,
            MovieOptionConfigError = 20241001, MovieOptionNoConfig = 20241002, MovieOptionNoOptionConfig = 20241003,
            CdKeyInvalid = 20054001;

        // No server-owned CD-key catalog exists in the client dump or AGENTS.md exceptions, so every key is invalid.
        [RequestPacketHandler("UseCdKeyRequest")]
        public static void UseCdKeyRequestHandler(Session session, Packet.Request packet)
        {
            packet.Deserialize<UseCdKeyRequest>();
            session.SendResponse(new UseCdKeyResponse { Code = CdKeyInvalid }, packet.Id);
        }

        // Mirrors XMovieAgency: the client only records options listed in MovieRecordOptions.
        [RequestPacketHandler("QueryMovieOptionsRequest")]
        public static void QueryMovieOptionsRequestHandler(Session session, Packet.Request packet)
        {
            QueryMovieOptionsRequest request = packet.Deserialize<QueryMovieOptionsRequest>();
            int code = TryGetMovie(request.MovieId, out _);
            session.SendResponse(new QueryMovieOptionsResponse
            {
                Code = code,
                MovieList = code != 0 ? [] : session.player.MovieOptions.FirstOrDefault(x => x.MovieId == request.MovieId)?.OptionIds.ToList() ?? []
            }, packet.Id);
        }

        [RequestPacketHandler("UpdateMovieOptionsRequest")]
        public static void UpdateMovieOptionsRequestHandler(Session session, Packet.Request packet)
        {
            UpdateMovieOptionsRequest request = packet.Deserialize<UpdateMovieOptionsRequest>();
            int code = TryGetMovie(request.MovieId, out MovieRecordOptionsTable? movie);
            if (code == 0 && !movie!.Options.Contains(request.OptionId))
                code = MovieOptionNoOptionConfig;
            if (code == 0)
            {
                MovieOptionsState? state = session.player.MovieOptions.FirstOrDefault(x => x.MovieId == request.MovieId);
                if (state is null)
                    session.player.MovieOptions.Add(state = new MovieOptionsState { MovieId = request.MovieId });
                if (!state.OptionIds.Contains(request.OptionId))
                {
                    state.OptionIds.Add(request.OptionId);
                    session.player.SaveChecked();
                }
            }
            session.SendResponse(new UpdateMovieOptionsResponse { Code = code }, packet.Id);
        }

        private static int TryGetMovie(string? movieId, out MovieRecordOptionsTable? movie)
        {
            movie = movieId is null ? null : TableReaderV2.Parse<MovieRecordOptionsTable>().FirstOrDefault(x => x.Id == movieId);
            return movie is null ? MovieOptionNoConfig : movie.Options.Count == 0 ? MovieOptionConfigError : 0;
        }

        internal static List<AutoFightRecordDto> BuildLoginRecords(Player player) =>
            player.AutoFightRecords.Select(ToDto).ToList();

        private static AutoFightRecordDto ToDto(AutoFightRecordState record) =>
            new() { StageId = record.StageId, CardIds = record.CardIds.ToList(), CompleteTime = record.CompleteTime };

        // Non-first-clear drop ids as FightSettle resolves them: FinishDropId rows (stage + level control), else the FinishRewardShow rows.
        internal static List<AscNet.Table.V2.share.reward.RewardGoodsTable> ResolveGoods(StageTable stage, int playerLevel)
        {
            StageLevelControlTable? control = FightModule.ResolveStageLevelControl((uint)stage.StageId, playerLevel);
            List<AscNet.Table.V2.share.reward.RewardGoodsTable> goods = RewardHandler.GetRewardGoods(stage.FinishDropId ?? 0);
            goods.AddRange(RewardHandler.GetRewardGoods(control?.FinishDropId ?? 0));
            if (goods.Count == 0)
            {
                goods = RewardHandler.GetRewardGoods(stage.FinishRewardShow ?? 0);
                goods.AddRange(RewardHandler.GetRewardGoods(control?.FinishRewardShow ?? 0));
            }
            return goods;
        }

        // XAutoFightManager.CheckAutoFightAvailable gates on AutoFightId > 0; AP uses XFubenAgency:GetRequireActionPoint
        // (the stage is passed by then, so FinishRequireActionPoint when RequireActionPoint is 0).
        private static int ActionPointCost(StageTable stage) =>
            stage.RequireActionPoint is > 0 ? stage.RequireActionPoint.Value : stage.FinishRequireActionPoint ?? 0;

        [RequestPacketHandler("StartAutoFightRequest")]
        public static void StartAutoFightRequestHandler(Session session, Packet.Request packet)
        {
            StartAutoFightRequest request = packet.Deserialize<StartAutoFightRequest>();
            int code = TryStartAutoFight(session, request, out AutoFightRecordState? record);
            session.SendResponse(new StartAutoFightResponse { Code = code, Record = record is null ? null : ToDto(record) }, packet.Id);
        }

        internal static int TryStartAutoFight(Session session, StartAutoFightRequest request, out AutoFightRecordState? record)
        {
            record = null;
            StageTable? stage = TableReaderV2.Parse<StageTable>().FirstOrDefault(x => x.StageId == request.StageId);
            AutoFightTable? config = stage?.AutoFightId is > 0
                ? TableReaderV2.Parse<AutoFightTable>().FirstOrDefault(x => x.Id == stage.AutoFightId) : null;
            if (stage is null || config is null)
                return AutoFightStageInvalid;
            if (!session.stage.Stages.TryGetValue((uint)request.StageId, out StageDatum? datum) || !datum.Passed)
                return AutoFightHaventPassed;
            if (session.player.AutoFightRecords.Any(x => x.StageId == request.StageId))
                return AutoFightAlreadyRunning;

            // XUiNewAutoFightDialog: remaining = MaxChallengeNums - PassTimesToday, capped by AutoFight.Limit when positive.
            long allowed = config.Limit;
            if (stage.MaxChallengeNums is > 0)
            {
                allowed = stage.MaxChallengeNums.Value - datum.PassTimesToday;
                if (config.Limit > 0)
                    allowed = Math.Min(allowed, config.Limit);
            }
            if (request.Times < 1 || request.Times > allowed)
                return AutoFightNotEnoughTimes;

            long cost = (long)ActionPointCost(stage) * request.Times;
            long held = session.inventory.Items.FirstOrDefault(x => x.Id == Inventory.ActionPoint)?.Count ?? 0;
            if (held < cost)
                return AutoFightNotEnoughActionPoint;

            // Duration per run is the stage's last recorded clear time (dialog: RecordTime * times).
            record = new AutoFightRecordState
            {
                StageId = request.StageId,
                CardIds = datum.LastCardIds.ToList(),
                Times = request.Times,
                CompleteTime = Clock().ToUnixTimeSeconds() + datum.LastRecordTime * request.Times
            };
            NotifyItemDataList items = new();
            if (cost > 0)
                items.ItemDataList.Add(session.inventory.Do(Inventory.ActionPoint, -(int)cost));
            session.player.AutoFightRecords.Add(record);
            session.player.SaveChecked();
            session.inventory.Save();
            if (cost > 0 && RepeatChallengeModule.IsStage((uint)request.StageId))
                RepeatChallengeModule.AddSerumSpentProgress(session, request.StageId, checked((int)cost));
            if (items.ItemDataList.Count > 0)
                session.SendPush(items);
            return 0;
        }

        [RequestPacketHandler("ObtainAutoFightRewardsRequest")]
        public static void ObtainAutoFightRewardsRequestHandler(Session session, Packet.Request packet)
        {
            ObtainAutoFightRewardsRequest request = packet.Deserialize<ObtainAutoFightRewardsRequest>();
            List<AutoFightRecordState> records = session.player.AutoFightRecords;
            if (request.Index < 0 || request.Index >= records.Count)
            {
                session.SendResponse(new ObtainAutoFightRewardsResponse { Code = AutoFightIndexInvalid }, packet.Id);
                return;
            }
            AutoFightRecordState record = records[request.Index];
            StageTable? stage = TableReaderV2.Parse<StageTable>().FirstOrDefault(x => x.StageId == record.StageId);
            if (record.StageId != request.StageId)
            {
                session.SendResponse(new ObtainAutoFightRewardsResponse { Code = AutoFightStageNotMatch }, packet.Id);
                return;
            }
            if (stage is null || Clock().ToUnixTimeSeconds() < record.CompleteTime)
            {
                session.SendResponse(new ObtainAutoFightRewardsResponse { Code = stage is null ? AutoFightStageInvalid : AutoFightNotComplete }, packet.Id);
                return;
            }

            // Same drop/exp sources as a non-first clear in FightSettle, times the queued runs.
            List<AscNet.Table.V2.share.reward.RewardGoodsTable> goods = ResolveGoods(stage, (int)session.player.PlayerData.Level);
            List<AscNet.Table.V2.share.reward.RewardGoodsTable> all = [];
            for (int i = 0; i < record.Times; i++) all.AddRange(goods);

            // XFubenAgency:GetTeamExp/GetCardExp fall back to Finish* when the base column is 0 for a passed stage.
            int teamExp = checked((stage.TeamExp is > 0 ? stage.TeamExp.Value : stage.FinishTeamExp ?? 0) * record.Times);
            int cardExp = checked((stage.CardExp is > 0 ? stage.CardExp.Value : stage.FinishCardExp ?? 0) * record.Times);
            // XAutoFightManager: robot stages report CharacterExp 0 (no owned character fights).
            bool robotStage = stage.RobotId?.Any(x => x > 0) == true;
            if (robotStage) cardExp = 0;

            RewardApplicationResult application = RewardHandler.ApplyRewards(all, session);
            NotifyItemDataList items = new();
            if (teamExp > 0)
                items.ItemDataList.Add(session.inventory.Do(Inventory.TeamExp, teamExp));
            session.ExpSanityCheck();
            NotifyCharacterDataList characters = new();
            if (cardExp > 0)
            {
                foreach (long id in record.CardIds.Where(x => x > 0).Distinct())
                    if (session.character.AddCharacterExp(checked((int)id), cardExp, (int)session.player.PlayerData.Level) is { } data)
                        characters.CharacterDataList.Add(data);
            }
            records.RemoveAt(request.Index);
            session.player.SaveChecked();
            session.inventory.Save();
            session.character.Save();

            application.SendPushes(session);
            if (items.ItemDataList.Count > 0) session.SendPush(items);
            if (characters.CharacterDataList.Count > 0) session.SendPush(characters);
            session.SendResponse(new ObtainAutoFightRewardsResponse
            {
                Code = 0, TeamExp = teamExp, CharacterExp = cardExp, Rewards = application.RewardGoods
            }, packet.Id);
        }
    }
}
