using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.equip;
using MessagePack;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

namespace AscNet.Test;

internal partial class Program
{
    private static void ValidateMentorGuidance()
    {
        PacketFactory.LoadPacketHandlers();
        ValidateCollaborationAndQualification("en");
        ValidateCollaborationAndQualification("cn");
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForBiancaCompatibility(
            out var players, out var characters, out var inventories, out _);
        Func<long> previousClock = MentorModule.Clock;
        long origin = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        origin -= TaskModule.WeeklyResetDayIndex(origin) * 86400L;
        long now = origin;
        MentorModule.Clock = () => now;
        try
        {
            using LoopbackSessionHarness learner = NewLearner(97_320, 19);
            Session session = learner.Session;
            AssertEqual(0, Login(learner).PlayerType, "guidance locked below the student tier");
            session.player.PlayerData.Level = 20;
            MentorModule.Refresh(session);
            NotifyMentorData first = ReadPushPayload<NotifyMentorData>(learner, "NotifyMentorChangeData", "student unlock");
            AssertEqual(2, first.PlayerType, "the guidance menu receives an eligible student");
            AssertEqual(0, first.Teacher.PlayerId, "no bound teacher required for weekly tasks");
            AssertEqual((uint)session.player.PlayerData.Id, first.Students.Single().PlayerId, "weekly tasks belong to the learner");
            AssertEqual(5, first.Students.Single().SystemTask.Count, "daily offer count from client config");
            AssertEqual(true, first.Students.Single().SystemTask.Any(task => task.TaskId == 40768 && task.Status == 0),
                "the 150 Serum objective is selectable");

            AssertEqual(20106042, Claim(learner, 1).Code, "cannot claim a milestone before completing a task");
            AssertEqual(20106050, Accept(learner, 40758), "locked War Zone objective cannot be accepted");
            AssertEqual(0, Accept(learner, 40768), "accept a Serum objective");
            AssertEqual(20106051, Accept(learner, 40768), "repeat acceptance does not create a second task");
            AssertEqual(0, Accept(learner, 40756), "accept a different Serum objective");
            AssertEqual(0, Accept(learner, 40769), "accept an authored login objective");
            AssertEqual(20106054, Accept(learner, 40757), "fourth daily acceptance is rejected");
            AssertEqual(0, Delete(learner, 40756), "abandon an incomplete task");

            MentorModule.RecordStageClear(session, 10040101, 1, 1, 149, false);
            var progress = ReadPushPayload<NotifyStudentWeeklyTaskChange>(learner, nameof(NotifyStudentWeeklyTaskChange), "partial progress");
            var serum = progress.WeeklyTask.Single(task => task.TaskId == 40768);
            AssertEqual(1, serum.Status, "partial task stays accepted");
            AssertEqual(149, serum.Schedule.Single().Value, "native progress bar receives the amount spent");
            MentorModule.RecordStageClear(session, 10040101, 1, 1, 1, false);
            AssertEqual(1, ReadPushPayload<NotifyStudentWeeklyTaskProgress>(learner, nameof(NotifyStudentWeeklyTaskProgress),
                "milestone notification").WeeklyTaskCompleteCount, "completing an objective advances weekly rewards");
            AssertEqual(2, ReadPushPayload<NotifyStudentWeeklyTaskChange>(learner, nameof(NotifyStudentWeeklyTaskChange),
                "completed task notification").WeeklyTask.Single(task => task.TaskId == 40768).Status, "native completed status");
            AssertEqual(20106052, Delete(learner, 40768), "cannot remove a completed objective and farm its milestone");
            AssertEqual(200, Claim(learner, 1).RewardGoodsList.Single(good => good.TemplateId == Inventory.TeamExp).Count,
                "the first low-level milestone grants the authored Commandant EXP");
            AssertEqual(23L, session.player.PlayerData.Level, "the reward converts EXP into Commandant levels immediately");
            AssertEqual(51L, session.inventory.SpendableCount(Inventory.TeamExp), "the remaining Commandant EXP is carried forward");

            session.player = BsonSerializer.Deserialize<Player>(players.LastReplacement!.ToBson());
            session.character = BsonSerializer.Deserialize<Character>(characters.LastReplacement!.ToBson());
            session.inventory = BsonSerializer.Deserialize<Inventory>(inventories.LastReplacement!.ToBson());
            NotifyMentorData restored = Login(learner);
            AssertEqual(1, restored.WeeklyTaskReward.Single(), "claimed milestone is restored on reconnect");
            AssertEqual(1, restored.WeeklyTaskCompleteCount, "completed task is restored on reconnect");
            AssertEqual(23L, session.player.PlayerData.Level, "the level gain is persisted with the XP reward");
            AssertEqual(20106041, Claim(learner, 1).Code, "retry after reconnect cannot grant duplicate EXP");

            now += 86400;
            session.player.PlayerData.LastLoginTime = now;
            MentorModule.Refresh(session);
            var daily = ReadPushPayload<NotifyStudentDailyReset>(learner, nameof(NotifyStudentDailyReset), "new day's offers");
            AssertEqual(true, daily.SystemTask.All(task => task.TaskId != 40768), "new day offers unaccepted objectives");
            AssertEqual(2, ReadPushPayload<NotifyStudentWeeklyTaskChange>(learner, nameof(NotifyStudentWeeklyTaskChange),
                "new login progress").WeeklyTask.Single(task => task.TaskId == 40769).Schedule.Single().Value, "distinct second login day");
            AssertEqual(2, Login(learner).Students.Single().WeeklyTask.Single(task => task.TaskId == 40769).Schedule.Single().Value,
                "reconnect on the same day does not advance the login objective");

            now = origin;
            using LoopbackSessionHarness advanced = NewLearner(97_360, 60);
            AssertEqual(60, Login(advanced).WeeklyLevel, "week starts in the level 60 reward tier");
            AssertEqual(0, Accept(advanced, 40768), "advanced learner has a Serum objective");
            MentorModule.RecordStageClear(advanced.Session, 10040101, 1, 1, 150, false);
            advanced.Session.player.PlayerData.Level = 80;
            AssertEqual(600, Claim(advanced, 1).RewardGoodsList.Single(good => good.TemplateId == Inventory.TeamExp).Count,
                "reaching instructor level keeps this week's higher-tier milestone claimable");
            now = origin + 7 * 86400;
            AssertEqual(1, Login(advanced).PlayerType, "the instructor tier begins at the next weekly reset");
            AssertEqual(20106022, Accept(advanced, 40768), "instructors cannot start a new student week");
            NotifyMentorData reset = Login(learner);
            AssertEqual(0, reset.WeeklyTaskCompleteCount, "completed count resets the next week");
            AssertEqual(0, reset.WeeklyTaskReward.Count, "milestone receipts are scoped to the week");
            AssertEqual(0, reset.Students.Single().WeeklyTask.Count, "accepted tasks reset the next week");

            using LoopbackSessionHarness interrupted = NewLearner(97_321, 20);
            _ = Login(interrupted);
            AssertEqual(0, Accept(interrupted, 40768), "start a claim recovery scenario");
            MentorModule.RecordStageClear(interrupted.Session, 10040101, 1, 1, 150, false);
            players.ThrowOnReplaceOne = true;
            try
            {
                _ = Claim(interrupted, 1);
                throw new InvalidDataException("Expected the Commandant level save to fail.");
            }
            catch (InvalidDataException exception) when (exception.InnerException is MongoException) { }
            finally { players.ThrowOnReplaceOne = false; }
            AssertEqual(20L, interrupted.Session.player.PlayerData.Level, "failed Player save rolls back the in-memory level");
            interrupted.Session.inventory = BsonSerializer.Deserialize<Inventory>(inventories.LastSuccessfulReplacementBson!);
            AssertEqual(23, interrupted.Session.inventory.PendingCommandantLevel, "inventory retains the committed level plan");
            AssertEqual(20106041, Claim(interrupted, 1).Code, "retry settles the level and does not grant the EXP a second time");
            AssertEqual(23L, interrupted.Session.player.PlayerData.Level, "retry commits the planned level");
            AssertEqual(51L, interrupted.Session.inventory.SpendableCount(Inventory.TeamExp), "retry preserves the planned EXP balance");
            AssertEqual(0, interrupted.Session.inventory.PendingCommandantLevel, "recovery clears the durable plan");

            using LoopbackSessionHarness solo = NewLearner(97_322, 20);
            solo.Session.player.ClientRegion = "cn";
            _ = Login(solo);
            int instructorId = MentorRequest<GetMentorRecommendPlayerListResponse>(solo, new GetMentorRecommendPlayerListRequest())
                .RecommendList.Single().PlayerId;
            AssertEqual(MentorModule.TemporaryInstructorId, instructorId, "recommendation offers the reserved temporary instructor");
            AssertEqual(20106002, MentorRequest<ApplyMentorResponse>(solo, new ApplyMentorRequest { TargetPlayerId = [1234] }).Code,
                "unrelated player id cannot be bound in solo guidance");
            AssertEqual(0, MentorRequest<ApplyMentorResponse>(solo, new ApplyMentorRequest { TargetPlayerId = [instructorId] }).Code,
                "bind the temporary instructor from the recruitment list");
            solo.Session.player = BsonSerializer.Deserialize<Player>(players.LastSuccessfulReplacementBson!);
            AssertEqual(instructorId, Login(solo).Teacher.PlayerId, "the temporary instructor persists on relog");
            AssertEqual(instructorId, MentorRequest<MentorGetNameListResponse>(solo, new MentorGetNameListRequest())
                .StudentNameList.MyTeacher!.PlayerId, "the mentorship record links the learner and temporary instructor");
            AssertEqual(20106038, MentorRequest<MentorGraduateResponse>(solo, new MentorGraduateRequest()).Code,
                "learner below graduation level cannot claim graduation rewards");
            AssertEqual(20106050, MentorRequest<StudentReceiveEquipResponse>(solo, new StudentReceiveEquipRequest { TaskId = 40768 }).Code,
                "cannot receive a gift for a task that was never accepted");
            AssertEqual(0, Accept(solo, 40768), "bound learner can take weekly tasks");
            MentorModule.RecordStageClear(solo.Session, 10040101, 1, 1, 150, false);
            _ = ReadPushPayload<NotifyStudentWeeklyTaskProgress>(solo, nameof(NotifyStudentWeeklyTaskProgress), "bound learner completion");
            AssertEqual(4, ReadPushPayload<NotifyStudentWeeklyTaskChange>(solo, nameof(NotifyStudentWeeklyTaskChange),
                "available instructor gift").WeeklyTask.Single().Status, "completed bound objective shows a claimable instructor gift");
            AssertEqual(300, MentorRequest<StudentReceiveEquipResponse>(solo, new StudentReceiveEquipRequest { TaskId = 40768 })
                .RewardGoodsList.Single().Count, "instructor grants the authored gift");
            AssertEqual(20106061, MentorRequest<StudentReceiveEquipResponse>(solo, new StudentReceiveEquipRequest { TaskId = 40768 }).Code,
                "an instructor gift is claimed only once");
            solo.Session.player.PlayerData.Level = 80;
            AssertEqual(2, Login(solo).PlayerType, "bound learner remains eligible until graduation");
            AssertEqual(0, MentorRequest<MentorStudentSendRewardResponse>(solo, new MentorStudentSendRewardRequest { MentorId = instructorId }).Code,
                "graduation-ready learner can thank the temporary instructor");
            AssertEqual(20106078, MentorRequest<MentorStudentSendRewardResponse>(solo, new MentorStudentSendRewardRequest { MentorId = instructorId }).Code,
                "gratitude can only be sent once");
            solo.Session.player.PlayerData.Level = 83;
            MentorModule.Refresh(solo.Session);
            AssertEqual(1, ReadPushPayload<NotifyMentorData>(solo, "NotifyMentorChangeData", "solo graduation").PlayerType,
                "the bound learner automatically graduates at the authored level");
            NotifyIGraduate? graduation = null;
            for (int i = 0; i < 8 && graduation is null; i++)
            {
                Packet.Push push = MessagePackSerializer.Deserialize<Packet.Push>(solo.ReadPacket("graduation rewards").Content);
                if (push.Name == nameof(NotifyIGraduate))
                    graduation = MessagePackSerializer.Deserialize<NotifyIGraduate>(push.Content);
            }
            AssertEqual(true, graduation?.RewardGoodsList.Any(good => good.TemplateId == Inventory.TeamExp) == true,
                "graduation settles earned weekly rewards along with its configured award");
            AssertEqual(20106049, MentorRequest<MentorGraduateResponse>(solo, new MentorGraduateRequest()).Code,
                "the graduation reward cannot be claimed again");
        }
        finally { MentorModule.Clock = previousClock; }

        LoopbackSessionHarness NewLearner(long uid, int level)
        {
            Player player = CreateDrawCompatibilityPlayer(uid);
            player.PlayerData.Level = level;
            player.PlayerData.LastLoginTime = now;
            return new(CreateDrawCompatibilityCharacter(uid), player, CreateDrawCompatibilityInventory(uid, []),
                "mentor-guidance", startClientLoop: false);
        }

        NotifyMentorData Login(LoopbackSessionHarness harness)
        {
            MentorModule.Refresh(harness.Session, login: true);
            return ReadPushPayload<NotifyMentorData>(harness, nameof(NotifyMentorData), "guidance login");
        }
    }

    private static void ValidateCollaborationAndQualification(string region)
    {
        const long uid = 97_318;
        const int recruitTask = 40_778;
        using var mongo = MongoCollectionOverride.InstallForBiancaCompatibility(out var players, out _, out _, out _);
        Player player = CreateDrawCompatibilityPlayer(uid);
        player.PlayerData.Level = 45;
        player.ClientRegion = region;
        using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(uid), player,
            CreateDrawCompatibilityInventory(uid, []), "guidance-collaboration", startClientLoop: false);
        Session session = harness.Session;
        session.stage = CreateLoginAccountCompatibilityStage(uid);
        session.GuildIdentityReady = true;

        int Value(int id) => TaskModule.BuildTaskData(session).Single(task => task.Id == id).Schedule.Single().Value;
        int ClaimTask(int id) => MentorRequest<FinishTaskResponse>(harness, new FinishTaskRequest { TaskId = id }).Code;
        int SendWorldChat(string content) => MentorRequest<SendChatResponse>(harness, new SendChatRequest
        {
            ChatData = new() { ChannelType = ChatChannelType.World, MsgType = ChatMsgType.Normal, Content = content }
        }).Code;

        var channels = MentorRequest<GetWorldChannelInfoResponse>(harness, new GetWorldChannelInfoRequest()).ChannelInfos;
        int recruitment = channels.Last().ChannelId;
        bool cn = region == "cn";
        AssertEqual(cn ? channels.Count : channels.Count - 1, recruitment,
            $"{region} client recognizes the last channel as recruitment");
        AssertEqual(20033013, MentorRequest<SelectChatChannelResponse>(harness,
            new SelectChatChannelRequest { ChannelId = cn ? recruitment - 1 : recruitment + 1 }).Code,
            $"{region} rejects an unlisted channel");
        AssertEqual(0, SendWorldChat("Ordinary channel test"), "Ordinary world chat is still usable");
        AssertEqual(0, Value(recruitTask), "Ordinary chat does not count as recruiting");
        AssertEqual(20026007, ClaimTask(recruitTask), "Recruitment reward requires a message");
        AssertEqual(0, MentorRequest<SelectChatChannelResponse>(harness,
            new SelectChatChannelRequest { ChannelId = recruitment }).Code, "Recruitment channel opens");
        AssertEqual(recruitment, MentorRequest<EnterWorldChatResponse>(harness, new EnterWorldChatRequest()).ChannelId,
            "Opening chat again retains the selected channel");
        AssertEqual(0, SendWorldChat("Recruitment channel test"), "A recruitment message is accepted");
        AssertEqual(1, Value(recruitTask), "Recruitment message advances the collaboration task");
        AssertEqual(0, ClaimTask(recruitTask), "Collaboration reward is claimable");
        AssertEqual(20026006, ClaimTask(recruitTask), "Collaboration reward cannot be claimed twice");

        AssertEqual(0, Value(40_750), "Qualification starts from actual saved stages");
        session.stage.AddStage(new() { StageId = 30_070_110, Passed = true });
        session.stage.AddStage(new() { StageId = 12_030_303, Passed = true });
        EquipTable sixStarWeapon = TableReaderV2.Parse<EquipTable>().First(equip => equip.Site == 0 && equip.Quality == 6);
        session.character.Equips.Add(new() { TemplateId = (uint)sixStarWeapon.Id, Breakthrough = 4, Level = 45 });
        session.character.Characters.Add(new() { Id = 1_021_001, Quality = 5, Level = 45, Grade = 1, LiberateLv = 3 });
        AssertEqual(1, Value(40_751), "Saved six-star weapon meets the qualification");
        AssertEqual(1, Value(40_752), "Saved SSS character meets the qualification");
        AssertEqual(0, Value(40_753), "A lower liberation level does not meet the qualification");
        session.character.Characters.Single(character => character.Id == 1_021_001).LiberateLv = 4;
        AssertEqual(1, Value(40_753), "Saved Ultima Rank meets the qualification");
        AssertEqual(1, Value(40_754), "Saved Stronghold clear meets the qualification");
        player.SimulatedBattlefield.BossLevelType = 2;
        player.SimulatedBattlefield.BossTotalScore = 1_000_000;
        AssertEqual(0, Value(40_755), "A different Pain Cage grade does not meet the qualification");
        player.SimulatedBattlefield.BossLevelType = 3;
        AssertEqual(1_000_000, Value(40_755), "Advanced Pain Cage score meets the qualification");
        AssertEqual(0, ClaimTask(40_750), "Qualification reward is claimable from saved progress");
        session.player = BsonSerializer.Deserialize<Player>(players.LastReplacement!.ToBson());
        AssertEqual(20026006, ClaimTask(40_750), "Qualification claim survives a save reload");

        session.player.MissionProgress.WeeklyResetWeek--;
        AssertEqual(0, Value(recruitTask), "The next week clears the collaboration task");
        AssertEqual(1, Value(40_750), "Weekly reset preserves qualification progress");
    }

    private static int Accept(LoopbackSessionHarness harness, int taskId) =>
        MentorRequest<StudentReceiveDailyTaskResponse>(harness, new StudentReceiveDailyTaskRequest { TaskId = taskId }).Code;

    private static int Delete(LoopbackSessionHarness harness, int taskId) =>
        MentorRequest<StudentDeleteDailyTaskResponse>(harness, new StudentDeleteDailyTaskRequest { TaskId = taskId }).Code;

    private static StudentGetTaskProgressRewardResponse Claim(LoopbackSessionHarness harness, int index) =>
        MentorRequest<StudentGetTaskProgressRewardResponse>(harness, new StudentGetTaskProgressRewardRequest { Index = index });

    private static TResponse MentorRequest<TResponse>(LoopbackSessionHarness harness, object request)
    {
        string name = request.GetType().Name;
        const int requestId = 97_300;
        InvokeRegisteredRequestHandler(name, harness.Session, requestId, request);
        for (int i = 0; i < 32; i++)
        {
            Packet packet = harness.ReadPacket(name);
            if (packet.Type == Packet.ContentType.Push)
                continue;
            AssertEqual(requestId, MessagePackSerializer.Deserialize<Packet.Response>(packet.Content).Id, name + " correlation");
            return ReadResponsePayload<TResponse>(packet, typeof(TResponse).Name);
        }
        throw new InvalidDataException($"Missing {typeof(TResponse).Name}.");
    }
}
