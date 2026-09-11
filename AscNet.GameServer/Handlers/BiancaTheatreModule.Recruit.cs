using AscNet.Common;
using AscNet.Common.MsgPack;
using AscNet.Table.V2.share.biancatheatre;
using AscNet.Table.V2.share.character.quality;
using MessagePack;

namespace AscNet.GameServer.Handlers;

[MessagePackObject(true)]
public sealed class BiancaTheatreSelectTeamRequest { public int TeamId { get; set; } }
[MessagePackObject(true)]
public sealed class BiancaTheatreSelectTeamResponse : BiancaTheatreResponse { }
[MessagePackObject(true)]
public sealed class BiancaTheatreSelectRecruitTickRequest { public int TickId { get; set; } }
[MessagePackObject(true)]
public sealed class BiancaTheatreSelectRecruitTickResponse : BiancaTheatreResponse { public BiancaTheatreStep? Step { get; set; } }
[MessagePackObject(true)]
public sealed class BiancaTheatreRecruitCharacterRequest { public int CharacterId { get; set; } }
[MessagePackObject(true)]
public sealed class BiancaTheatreRecruitCharacterResponse : BiancaTheatreResponse { }
[MessagePackObject(true)]
public sealed class BiancaTheatreRecruitRefreshResponse : BiancaTheatreResponse { public List<int> CharacterIds { get; set; } = []; }
[MessagePackObject(true)]
public sealed class BiancaTheatreEndRecruitResponse : BiancaTheatreResponse { public List<BiancaTheatreItem> BiancaTheatreItems { get; set; } = []; }
[MessagePackObject(true)]
public sealed class BiancaTheatreSetSingleTeamRequest { public BiancaTheatreTeamData? TeamData { get; set; } }
[MessagePackObject(true)]
public sealed class BiancaTheatreSetSingleTeamResponse : BiancaTheatreResponse { }

internal static partial class BiancaTheatreModule
{
    private static ServerCodeException RecruitInvalid(string message) => new(message, 1);
    private static int RecruitConfig(string key) => Rows<BiancaTheatreConfigTable>().Single(x => x.Key == key).Value;
    private static BiancaTheatreStep RecruitStep(Mutation m)
    {
        var step = CurrentStep(m);
        if (step == null || step.StepType is not (4 or 7)) throw RecruitInvalid("No active recruitment.");
        return step;
    }

    public static void BeginRun(Mutation m)
    {
        if (m.Data.CurTeamId <= 0 || m.Data.CurChapterDb == null || m.Data.CurChapterDb.Steps.Count != 0)
            throw RecruitInvalid("Adventure is not awaiting initialization.");
        // These account items represent the current run only, unlike the outer metaprogression currency.
        foreach (int itemId in new[] { InnerCoin, ActionPoint, VisionItem })
        {
            long balance = m.Balance(itemId);
            if (balance > 0) m.AddCost(itemId, checked((int)balance));
        }
        var effects = SystemEffects(m).ToList();
        foreach (var effect in effects.Where(x => x.Type is 1 or 10))
        {
            m.AddGoods(checked((int)effect.Params[0]), checked((int)effect.Params[1]));
            m.State.AppliedSystemEffectIds.Add(effect.Id);
        }
        foreach (var effect in effects.Where(x => x.Type == 14))
        {
            foreach (int itemId in DrawItemBox(m, checked((int)effect.Params[0]))) GrantInnerItem(m, itemId);
            m.State.AppliedSystemEffectIds.Add(effect.Id);
        }
        BeginChapter(m);
    }

    public static void BeginChapter(Mutation m)
    {
        var chapter = m.Data.CurChapterDb ?? throw RecruitInvalid("No chapter to initialize.");
        if (chapter.Steps.Count != 0) throw RecruitInvalid("Chapter is already initialized.");
        var config = Rows<BiancaTheatreChapterTable>().Single(x => x.Id == chapter.ChapterId);
        foreach (var effect in SystemEffects(m).Where(x => x.Type == 15 && x.Params[0] == chapter.ChapterId).ToList())
            foreach (int item in DrawItemBox(m, checked((int)effect.Params[1]))) GrantInnerItem(m, item);
        if (config.ExtraRewardItemBoxId is > 0 && Condition(m, config.ExtraRewardConditionId ?? 0))
            m.State.QueuedSteps.Add(new BiancaTheatreStep
            {
                Uid = Uid(m), StepType = 1, IsExtraReward = 1,
                ItemIds = DrawItemBox(m, config.ExtraRewardItemBoxId.Value)
            });
        if (config.DefaultRecruitTicketId is > 0)
        {
            var defaults = Rows<BiancaTheatreDefaultRecruitTicketTable>().Single(x => x.Id == config.DefaultRecruitTicketId.Value);
            m.State.QueuedSteps.Add(new BiancaTheatreStep
            {
                Uid = Uid(m), StepType = 3, TickIds = RecruitOneSpecialDefaultTickets(m, defaults)
            });
        }
        ContinueRun(m);
    }

    private static List<int> RecruitOneSpecialDefaultTickets(Mutation m, BiancaTheatreDefaultRecruitTicketTable defaults)
    {
        // Approved private-server rule: uniformly replace one eligible paired normal slot with its special ticket.
        // EN/CN and the installed 4.7 table supply the pairs, but do not disclose retail replacement odds.
        var tickets = defaults.NormalId.ToList();
        var rows = Rows<BiancaTheatreRecruitTicketTable>();
        if (tickets.Count == 0 || tickets.Any(id => !rows.Any(x => x.Id == id)))
            throw RecruitInvalid("Invalid default recruitment ticket configuration.");
        var eligible = Enumerable.Range(0, Math.Min(tickets.Count, defaults.SpecialId.Count))
            .Where(index => rows.Any(x => x.Id == defaults.SpecialId[index] && x.GroupId.Any(group =>
                Rows<BiancaTheatreRecruitCharacterGroupTable>().Any(candidate => candidate.GroupId == group &&
                    candidate.Weight > 0 && RecruitEligible(m, candidate.CharacterId, false))))).ToList();
        if (eligible.Count > 0)
        {
            int index = PickWeighted(eligible, _ => 1);
            tickets[index] = defaults.SpecialId[index];
        }
        return tickets;
    }

    [RequestPacketHandler("BiancaTheatreSelectTeamRequest")]
    public static void BiancaTheatreSelectTeamRequestHandler(Session session, Packet.Request packet) =>
        Handle<BiancaTheatreSelectTeamRequest, BiancaTheatreSelectTeamResponse>(session, packet, (m, request, response) =>
        {
            if (m.Data.CurChapterDb == null) throw RecruitInvalid("No active adventure.");
            if (m.Data.CurTeamId == request.TeamId && request.TeamId > 0) return;
            if (m.Data.CurTeamId != 0) throw RecruitInvalid("Adventure team already selected.");
            var team = Rows<BiancaTheatreTeamTable>().SingleOrDefault(x => x.Id == request.TeamId);
            if (team == null || !Condition(m, team.ConditionId ?? 0)) throw RecruitInvalid("Adventure team unavailable.");
            m.Data.CurTeamId = team.Id;
            BeginRun(m);
        });

    public static void AppendTicketStep(Mutation m, IReadOnlyList<int> ticketIds, int rootUid = 0)
    {
        if (ticketIds.Count == 0 || ticketIds.Distinct().Count() != ticketIds.Count ||
            ticketIds.Any(id => !Rows<BiancaTheatreRecruitTicketTable>().Any(x => x.Id == id)))
            throw RecruitInvalid("Invalid recruitment ticket choices.");
        RecruitAppendOrQueue(m, new BiancaTheatreStep { StepType = 3, TickIds = ticketIds.ToList(), RootUid = rootUid });
    }

    public static void AppendRecruitStep(Mutation m, int ticketId, int rootUid = 0, bool decay = false) =>
        RecruitAppendOrQueue(m, RecruitCreateStep(m, ticketId, rootUid, decay));

    private static void RecruitAppendOrQueue(Mutation m, BiancaTheatreStep step)
    {
        var current = CurrentStep(m);
        if (step.RootUid > 0 && current != null && current.Uid != step.RootUid && current.RootUid == step.RootUid)
        {
            step.Uid = Uid(m);
            m.State.QueuedSteps.Add(step);
        }
        else AppendStep(m, step);
    }

    private static BiancaTheatreStep RecruitCreateStep(Mutation m, int ticketId, int rootUid, bool decay)
    {
        var step = new BiancaTheatreStep { StepType = decay ? 7 : 4, TickId = ticketId, RootUid = rootUid };
        if (decay)
        {
            var ticket = Rows<BiancaTheatreDecayRecruitTicketTable>().SingleOrDefault(x => x.Id == ticketId)
                ?? throw RecruitInvalid("Unknown assimilation ticket.");
            step.RefreshCount = ticket.RefreshCount;
            step.RecruitCount = ticket.RecruitCount;
        }
        else
        {
            var ticket = Rows<BiancaTheatreRecruitTicketTable>().SingleOrDefault(x => x.Id == ticketId)
                ?? throw RecruitInvalid("Unknown recruitment ticket.");
            step.RefreshCount = ticket.RefreshCount;
            step.RecruitCount = ticket.RecruitCount;
        }
        foreach (var effect in SystemEffects(m))
        {
            if (effect.Type == 5) step.RefreshCount = checked(step.RefreshCount + checked((int)effect.Params[0]));
            if (effect.Type == 6) step.RecruitCount = checked(step.RecruitCount + checked((int)effect.Params[0]));
        }
        if (SystemEffects(m).Any(x => x.Type == 25)) step.RefreshCount = 0;
        RecruitDraw(m, step);
        return step;
    }

    private static bool RecruitEligible(Mutation m, int id, bool decay)
    {
        var character = m.Data.Characters.SingleOrDefault(x => x.CharacterId == id);
        if (decay) return character != null && character.IsDecay == 0 &&
            Rows<BiancaTheatreCharacterLevelTable>().Any(x => x.CharacterId == id && x.Type == 2 && x.Level == character.Level + 1);
        int level = character?.Level ?? 0;
        int max = RecruitConfig(character?.IsDecay > 0 ? "DecayMaxCharacterLevel" : "MaxCharacterLevel");
        return level < max && Rows<BiancaTheatreBaseCharacterTable>().Any(x => x.CharacterId == id) &&
            Rows<BiancaTheatreCharacterLevelTable>().Any(x => x.CharacterId == id && x.Level == level + 1 && x.Type == (character?.IsDecay > 0 ? 2 : 1));
    }

    private static void RecruitDraw(Mutation m, BiancaTheatreStep step)
    {
        bool decay = step.StepType == 7;
        step.RefreshCharacterIds.Clear();
        step.FloorIndexes.Clear();
        step.RecruitCharacterIds.Clear();
        if (decay)
        {
            // Approved private-server rule: uniform owned, non-assimilated sampling without replacement.
            // XUiBiancaTheatreRecruit:Set3DCharacter defines exactly three presentation slots.
            var eligible = m.Data.Characters.Where(x => RecruitEligible(m, x.CharacterId, true)).Select(x => x.CharacterId).ToList();
            for (int slot = 0; slot < 3; slot++)
            {
                int id = eligible.Count == 0 ? 0 : PickWeighted(eligible, _ => 1);
                step.RefreshCharacterIds.Add(id);
                step.FloorIndexes.Add(0);
                eligible.Remove(id);
            }
            return;
        }
        int floorGroup = RecruitConfig("FloorCharacterGroupId");
        var groups = Rows<BiancaTheatreRecruitTicketTable>().Single(x => x.Id == step.TickId).GroupId;
        var source = Rows<BiancaTheatreRecruitCharacterGroupTable>();
        var effects = SystemEffects(m);
        decimal rankFactor = effects.Where(x => x.Type == 13).Aggregate(1m, (factor, x) => factor * checked((decimal)x.Params[0]));
        if (rankFactor <= 0) throw RecruitInvalid("Invalid recruitment weight multiplier.");
        // Scale all weights together, preserving fractional rank bonuses without rounding individual candidates.
        int weightScale = checked((int)Math.Pow(10, (decimal.GetBits(rankFactor)[3] >> 16) & 0xff));
        var sRanks = Rows<CharacterQualityTable>().GroupBy(x => x.CharacterId)
            .Where(x => x.Min(y => y.Quality) == 3).Select(x => (int)x.Key).ToHashSet();
        foreach (int group in groups)
        {
            var pool = source.Where(x => x.GroupId == group && x.Weight > 0 &&
                !step.RefreshCharacterIds.Contains(x.CharacterId) && RecruitEligible(m, x.CharacterId, decay)).ToList();
            int floor = 0;
            if (pool.Count == 0 && group != floorGroup)
            {
                pool = source.Where(x => x.GroupId == floorGroup && x.Weight > 0 &&
                    !step.RefreshCharacterIds.Contains(x.CharacterId) && RecruitEligible(m, x.CharacterId, decay)).ToList();
                floor = 1;
            }
            int id = pool.Count == 0 ? 0 : PickWeighted(pool,
                x => checked((int)(x.Weight * weightScale * (sRanks.Contains(x.CharacterId) ? rankFactor : 1m)))).CharacterId;
            step.RefreshCharacterIds.Add(id);
            step.FloorIndexes.Add(floor);
        }
    }

    [RequestPacketHandler("BiancaTheatreSelectRecruitTickRequest")]
    public static void BiancaTheatreSelectRecruitTickRequestHandler(Session session, Packet.Request packet) =>
        Handle<BiancaTheatreSelectRecruitTickRequest, BiancaTheatreSelectRecruitTickResponse>(session, packet, (m, request, response) =>
        {
            var step = CurrentStep(m) ?? throw RecruitInvalid("No ticket selection.");
            if (step.StepType == 4 && step.TickId == request.TickId)
            {
                response.Step = step;
                return;
            }
            if (step.StepType != 3 || !step.TickIds.Contains(request.TickId)) throw RecruitInvalid("Ticket not offered.");
            step.TickId = request.TickId;
            response.Step = RecruitCreateStep(m, request.TickId, step.RootUid == 0 ? step.Uid : step.RootUid, false);
            AppendStep(m, response.Step, notify: false);
        });

    [RequestPacketHandler("BiancaTheatreRecruitRefreshRequest")]
    public static void BiancaTheatreRecruitRefreshRequestHandler(Session session, Packet.Request packet) =>
        Handle<BiancaTheatreEmptyRequest, BiancaTheatreRecruitRefreshResponse>(session, packet, (m, request, response) =>
        {
            var step = RecruitStep(m);
            if (step.CurRefreshCount >= step.RefreshCount || SystemEffects(m).Any(x => x.Type == 25))
                throw RecruitInvalid("No recruitment refresh attempts.");
            RecruitDraw(m, step);
            step.CurRefreshCount = checked(step.CurRefreshCount + 1);
            response.CharacterIds = step.RefreshCharacterIds.ToList();
            // EN consumes an attempt even when every candidate slot is empty.
            if (response.CharacterIds.All(x => x == 0)) response.Code = 20176010;
        });

    [RequestPacketHandler("BiancaTheatreRecruitCharacterRequest")]
    public static void BiancaTheatreRecruitCharacterRequestHandler(Session session, Packet.Request packet) =>
        Handle<BiancaTheatreRecruitCharacterRequest, BiancaTheatreRecruitCharacterResponse>(session, packet, (m, request, response) =>
        {
            var step = RecruitStep(m);
            if (step.RecruitCharacterIds.Contains(request.CharacterId)) return;
            bool decay = step.StepType == 7;
            if (step.CurRecruitCount >= step.RecruitCount || !step.RefreshCharacterIds.Contains(request.CharacterId) ||
                !RecruitEligible(m, request.CharacterId, decay)) throw RecruitInvalid("Character cannot be recruited.");
            var character = m.Data.Characters.SingleOrDefault(x => x.CharacterId == request.CharacterId);
            if (character == null)
                m.Data.Characters.Add(new BiancaTheatreCharacter { CharacterId = request.CharacterId, Level = 1 });
            else
            {
                character.Level = checked(character.Level + 1);
                if (decay) character.IsDecay = 1;
            }
            step.RecruitCharacterIds.Add(request.CharacterId);
            step.CurRecruitCount = checked(step.CurRecruitCount + 1);
            m.State.RunRecruitCount = checked(m.State.RunRecruitCount + 1);
            m.State.TotalRecruitCount = checked(m.State.TotalRecruitCount + 1);
        });

    [RequestPacketHandler("BiancaTheatreEndRecruitRequest")]
    public static void BiancaTheatreEndRecruitRequestHandler(Session session, Packet.Request packet) =>
        Handle<BiancaTheatreEmptyRequest, BiancaTheatreEndRecruitResponse>(session, packet, (m, request, response) =>
        {
            var current = CurrentStep(m);
            if (current?.StepType is not (4 or 7))
            {
                if (m.Data.CurChapterDb?.Steps.LastOrDefault(x => x.StepType is 4 or 7)?.Overdue == 1) return;
                throw RecruitInvalid("No recruitment to finish.");
            }
            int least = current.StepType == 7
                ? Rows<BiancaTheatreDecayRecruitTicketTable>().Single(x => x.Id == current.TickId).LeastRecruitCount
                : Rows<BiancaTheatreRecruitTicketTable>().Single(x => x.Id == current.TickId).LeastRecruitCount;
            if (current.CurRecruitCount < least && current.CurRecruitCount < current.RecruitCount &&
                current.CurRefreshCount < current.RefreshCount && current.RefreshCharacterIds.Any(id =>
                    id > 0 && !current.RecruitCharacterIds.Contains(id) && RecruitEligible(m, id, current.StepType == 7)))
                throw RecruitInvalid("Recruitment minimum not reached.");
            if (current.StepType == 7 && current.CurRecruitCount > 0)
            {
                var ticket = Rows<BiancaTheatreDecayRecruitTicketTable>().Single(x => x.Id == current.TickId);
                var before = m.Data.Items.Select(x => x.Uid).ToHashSet();
                // Approved private-server timing: both configured assimilation rewards commit at EndRecruit.
                if (ticket.InnerItemId > 0) GrantInnerItem(m, ticket.InnerItemId);
                if (ticket.VisionId > 0)
                {
                    var vision = Rows<BiancaTheatreVisionChangeTable>().Single(x => x.Id == ticket.VisionId);
                    long oldValue = m.Balance(VisionItem);
                    long newValue = Math.Clamp(oldValue + vision.Change, 0, Rows<BiancaTheatreVisionTable>().Max(x => x.Max));
                    if (newValue > oldValue) m.AddGoods(VisionItem, checked((int)(newValue - oldValue)));
                    else if (newValue < oldValue) m.AddCost(VisionItem, checked((int)(oldValue - newValue)));
                    m.Push(new NotifyBiancaTheatreVisionChange { VisionId = ticket.VisionId });
                }
                response.BiancaTheatreItems = m.Data.Items.Where(x => !before.Contains(x.Uid)).ToList();
            }
            current.Overdue = 1;
            var ticketRoot = m.Data.CurChapterDb!.Steps.SingleOrDefault(x => x.Uid == current.RootUid && x.StepType == 3);
            if (ticketRoot != null) ticketRoot.Overdue = 1;
            ContinueRun(m);
        });

    private static void RecruitValidateTeam(Mutation m, BiancaTheatreTeamData? team, HashSet<int> used)
    {
        if (m.Data.CurChapterDb == null || team == null || team.CardIds == null || team.RobotIds == null ||
            team.CardIds.Count != 3 || team.RobotIds.Count != 3 || team.CaptainPos is < 1 or > 3 ||
            team.FirstFightPos is < 1 or > 3 || team.EnterCgIndex is < 0 or > 3 || team.SettleCgIndex is < 0 or > 3)
            throw RecruitInvalid("Invalid combat team shape.");
        var occupied = new bool[3];
        for (int i = 0; i < 3; i++)
        {
            int card = team.CardIds[i], robot = team.RobotIds[i];
            if (card < 0 || robot < 0 || (card > 0 && robot > 0)) throw RecruitInvalid("Invalid combat team slot.");
            if (card == 0 && robot == 0) continue;
            int characterId = card;
            if (robot > 0)
            {
                var matches = m.Data.Characters.Where(role => Rows<BiancaTheatreCharacterLevelTable>().Any(x =>
                    x.RobotId == robot && x.CharacterId == role.CharacterId && x.Level == role.Level &&
                    x.Type == (role.IsDecay > 0 ? 2 : 1))).ToList();
                if (matches.Count != 1) throw RecruitInvalid("Robot does not uniquely match a recruited role.");
                characterId = matches[0].CharacterId;
            }
            else if (!m.Session.character.Characters.Any(x => x.Id == card))
                throw RecruitInvalid("Local character is not owned.");
            if (!m.Data.Characters.Any(x => x.CharacterId == characterId) || !used.Add(characterId))
                throw RecruitInvalid("Role is unavailable or already deployed.");
            occupied[i] = true;
        }
        if (!occupied[team.CaptainPos - 1] || !occupied[team.FirstFightPos - 1] ||
            (m.Data.TeamCountEffect > 0 && occupied.Count(x => x) > m.Data.TeamCountEffect))
            throw RecruitInvalid("Combat team captain, first fighter or size is invalid.");
    }

    private static bool RecruitSameTeam(BiancaTheatreTeamData? left, BiancaTheatreTeamData? right) =>
        left != null && right != null && left.TeamIndex == right.TeamIndex &&
        left.CaptainPos == right.CaptainPos && left.FirstFightPos == right.FirstFightPos &&
        left.EnterCgIndex == right.EnterCgIndex && left.SettleCgIndex == right.SettleCgIndex &&
        left.CardIds != null && right.CardIds != null && left.CardIds.SequenceEqual(right.CardIds) &&
        left.RobotIds != null && right.RobotIds != null && left.RobotIds.SequenceEqual(right.RobotIds);

    [RequestPacketHandler("BiancaTheatreSetSingleTeamRequest")]
    public static void BiancaTheatreSetSingleTeamRequestHandler(Session session, Packet.Request packet) =>
        Handle<BiancaTheatreSetSingleTeamRequest, BiancaTheatreSetSingleTeamResponse>(session, packet, (m, request, response) =>
        {
            if (m.Data.CurChapterDb != null && RecruitSameTeam(request.TeamData, m.Data.SingleTeamData)) return;
            if (request.TeamData?.TeamIndex != 0 || m.State.Fight?.FightId > 0) throw RecruitInvalid("Cannot change combat team.");
            RecruitValidateTeam(m, request.TeamData, []);
            m.Data.SingleTeamData = request.TeamData!;
        });

}
