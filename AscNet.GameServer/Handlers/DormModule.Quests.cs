using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.dormitory.character;
using AscNet.Table.V2.share.dormitory.quest;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MessagePack;

namespace AscNet.GameServer.Handlers;

[MessagePackObject(true)]
public sealed class QuestUpgradeTerminalLvRequest { }
[MessagePackObject(true)]
public sealed class QuestUpgradeTerminalLvResponse { public int Code { get; set; } public uint TerminalUpgradeTime { get; set; } }
[MessagePackObject(true)]
public sealed class QuestAcceptRequest { public List<QuestAcceptParam> QuestAcceptParams { get; set; } = []; }
[MessagePackObject(true)]
public sealed class QuestAcceptParam { public int Index { get; set; } public List<uint> TeamCharacter { get; set; } = []; }
[MessagePackObject(true)]
public sealed class QuestAcceptResponse { public int Code { get; set; } public List<NotifyDormitoryData.NotifyDormitoryDataDormQuestAccept> QuestAccept { get; set; } = []; }
[MessagePackObject(true)]
public sealed class QuestGetAllRewardRequest { }
[MessagePackObject(true)]
public sealed class QuestGetAllRewardResponse { public int Code { get; set; } public List<QuestFinishInfo> FinishQuestInfos { get; set; } = []; public QuestDormQuestUpdate DormQuestUpdate { get; set; } = new(); }
[MessagePackObject(true)]
public sealed class QuestFinishInfo { public int QuestId { get; set; } public List<uint> TeamCharacter { get; set; } = []; public int FinishReward { get; set; } public int ExtraReward { get; set; } public int FileId { get; set; } }
[MessagePackObject(true)]
public sealed class QuestDormQuestUpdate { public List<NotifyDormitoryData.NotifyDormitoryDataDormQuestAccept> QuestAccept { get; set; } = []; public List<NotifyDormitoryData.NotifyDormitoryDataDormCollectFile> CollectFiles { get; set; } = []; public int TerminalUpgradeExp { get; set; } }
[MessagePackObject(true)]
public sealed class QuestRecallTeamRequest { public int Index { get; set; } public int ResetCount { get; set; } }
[MessagePackObject(true)]
public sealed class QuestRecallTeamResponse { public int Code { get; set; } public List<NotifyDormitoryData.NotifyDormitoryDataDormQuestAccept> QuestAccept { get; set; } = []; }
[MessagePackObject(true)]
public sealed class QuestReadFileRequest { public int FileId { get; set; } }
[MessagePackObject(true)]
public sealed class QuestReadFileResponse { public int Code { get; set; } }
[MessagePackObject(true)]
public sealed class NotifyDormQuestTerminalInit { public int TerminalLv { get; set; } public List<NotifyDormitoryData.NotifyDormitoryDataDormQuest> TotalQuest { get; set; } = []; }
[MessagePackObject(true)]
public sealed class NotifyDormQuestData { public List<NotifyDormitoryData.NotifyDormitoryDataDormQuest> TotalQuest { get; set; } = []; public List<NotifyDormitoryData.NotifyDormitoryDataDormQuestAccept> QuestAccept { get; set; } = []; }

internal partial class DormModule
{
    private const int QuestTerminalMaxLv = 20060065;
    private const int QuestTerminalUpgradeOngoing = 20060066;
    private const int QuestFinishQuestNotEnough = 20060067;
    private const int QuestNotAccept = 20060068;
    private const int QuestAlreadyAccept = 20060069;
    private const int QuestCfgNotExist = 20060070;
    private const int QuestNotExist = 20060071;
    private const int QuestIndexRepeat = 20060072;
    private const int QuestTeamCharacterRepeat = 20060073;
    private const int QuestTeamCharacterNotEnough = 20060074;
    private const int QuestCharacterIsInQuest = 20060075;
    private const int QuestTerminalIdleCountNotEnough = 20060076;
    private const int QuestAlreadyFinish = 20060077;
    private const int QuestTerminalCfgError = 20060078;
    private const int QuestFileNotCollect = 20060079;
    private const int QuestConditionNotFinish = 20060080;

    internal static NotifyDormitoryData.NotifyDormitoryDataDormQuestData BuildQuestLoginData(Session session)
    {
        NormalizeQuestState(session);
        PlayerDormQuestState state = session.player.Dorm.Quest;
        return new NotifyDormitoryData.NotifyDormitoryDataDormQuestData
        {
            ResetCount = state.ResetCount, TerminalLv = state.TerminalLv, TerminalUpgradeExp = state.TerminalUpgradeExp,
            FinishQuestCount = state.FinishQuestCount, TerminalUpgradeTime = state.TerminalUpgradeTime,
            TerminalUpgradeStatus = state.TerminalUpgradeStatus, FinishQuests = state.FinishQuests.Select(row => row.ToList()).ToList(),
            TriggerLimitedQuest = state.TriggerLimitedQuest.ToList(), TotalQuest = state.TotalQuest.Select(Quest).ToList(),
            QuestAccept = state.QuestAccept.Select(Accept).ToList(), CollectFiles = state.CollectFiles.Select(File).ToList()
        };
    }

    [RequestPacketHandler("QuestUpgradeTerminalLvRequest")]
    public static void QuestUpgradeTerminalLvRequestHandler(Session session, Packet.Request packet)
    {
        NormalizeQuestState(session);
        PlayerDormQuestState state = session.player.Dorm.Quest;
        QuestTerminalTable? terminal = Terminal(state.TerminalLv);
        int code = terminal is null ? QuestTerminalCfgError
            : terminal.NeedTime <= 0 ? QuestTerminalMaxLv
            : state.TerminalUpgradeStatus != 0 ? QuestTerminalUpgradeOngoing
            : state.TerminalUpgradeExp < terminal.NeedFinishQuest ? QuestFinishQuestNotEnough
            : !CanPay(session, terminal) ? 20060003 : 0;
        if (code == 0)
        {
            Inventory inventory = BsonSerializer.Deserialize<Inventory>(session.inventory.ToBson());
            int status = state.TerminalUpgradeStatus;
            uint time = state.TerminalUpgradeTime;
            try
            {
                NotifyItemDataList items = Pay(session, terminal!);
                session.inventory.SaveChecked();
                state.TerminalUpgradeStatus = 1;
                state.TerminalUpgradeTime = QuestNow();
                session.player.SaveChecked();
                if (items.ItemDataList.Count > 0) session.SendPush(items);
            }
            catch
            {
                state.TerminalUpgradeStatus = status;
                state.TerminalUpgradeTime = time;
                session.inventory.Items = inventory.Items;
                session.inventory.AppliedRewardClaims = inventory.AppliedRewardClaims;
                try { session.inventory.SaveChecked(); } catch { }
                code = QuestTerminalCfgError;
            }
        }
        session.SendResponse(new QuestUpgradeTerminalLvResponse { Code = code, TerminalUpgradeTime = state.TerminalUpgradeTime }, packet.Id);
    }

    [RequestPacketHandler("QuestAcceptRequest")]
    public static void QuestAcceptRequestHandler(Session session, Packet.Request packet)
    {
        NormalizeQuestState(session);
        QuestAcceptRequest request = packet.Deserialize<QuestAcceptRequest>();
        RefillExhaustedBoard(session);
        PlayerDormQuestState state = session.player.Dorm.Quest;
        int code = ValidateAccept(session, request);
        if (code == 0)
        {
            foreach (QuestAcceptParam parameter in request.QuestAcceptParams)
            {
                PlayerDormQuest board = state.TotalQuest.Single(quest => quest.Index == parameter.Index);
                QuestTable quest = TableReaderV2.Parse<QuestTable>().Single(row => row.Id == board.QuestId);
                state.QuestAccept.Add(new PlayerDormQuestAccept
                {
                    QuestId = board.QuestId, FileId = SelectFile(session, quest, parameter.TeamCharacter, board.ResetCount, board.Index), Index = board.Index, IsSpecialQuest = board.IsSpecialQuest,
                    ResetCount = board.ResetCount, TeamCharacter = parameter.TeamCharacter.ToList(), AcceptTime = QuestNow(),
                    IsSatisfyRecommend = IsRecommended(parameter.TeamCharacter, quest), IsAward = false
                });
            }
            session.player.SaveChecked();
            TaskModule.RecordTableDrivenProgress(session, [(29018, null, request.QuestAcceptParams.Count)]);
        }
        session.SendResponse(new QuestAcceptResponse { Code = code, QuestAccept = code == 0 ? state.QuestAccept.Select(Accept).ToList() : [] }, packet.Id);
    }

    [RequestPacketHandler("QuestGetAllRewardRequest")]
    public static void QuestGetAllRewardRequestHandler(Session session, Packet.Request packet)
    {
        PlayerDormQuestState state = session.player.Dorm.Quest;
        uint now = QuestNow();
        Dictionary<int, QuestTable> quests = TableReaderV2.Parse<QuestTable>().ToDictionary(row => row.Id);
        List<PlayerDormPendingReward> pendingRewards = session.player.Dorm.PendingRewards.Where(pending => pending.Key.StartsWith($"dorm-quest:{session.player.PlayerData.Id}:", StringComparison.Ordinal)).ToList();
        List<PlayerDormQuestAccept> completed = pendingRewards.Count > 0
            ? state.QuestAccept.Where(accept => pendingRewards.Any(pending => pending.Key.StartsWith(QuestClaimPrefix(session, accept), StringComparison.Ordinal))).ToList()
            : state.QuestAccept.Where(accept => !accept.IsAward && quests.TryGetValue(accept.QuestId, out QuestTable? quest) && now >= accept.AcceptTime + quest.NeedTime).ToList();
        if (completed.Count == 0)
        {
            session.SendResponse(new QuestGetAllRewardResponse { Code = QuestAlreadyFinish, DormQuestUpdate = Update(state) }, packet.Id);
            return;
        }
        List<RewardGrant> grants = pendingRewards.Count > 0
            ? pendingRewards.Where(pending => completed.Any(accept => pending.Key.StartsWith(QuestClaimPrefix(session, accept), StringComparison.Ordinal))).Select(pending => new RewardGrant(pending.Key, pending.Goods.Select(good => new AscNet.Table.V2.share.reward.RewardGoodsTable { Id = good.Id, TemplateId = good.TemplateId, Count = good.Count, Params = good.Params.ToList() }).ToList())).ToList()
            : completed.SelectMany(accept => Grants(session, accept, quests[accept.QuestId])).ToList();
        if (pendingRewards.Count == 0)
        {
            int finishQuestCount = state.FinishQuestCount;
            int terminalUpgradeExp = state.TerminalUpgradeExp;
            List<List<int>> finishQuests = state.FinishQuests.Select(row => row.ToList()).ToList();
            List<PlayerDormCollectFile> files = state.CollectFiles.Select(file => new PlayerDormCollectFile { FileId = file.FileId, IsRead = file.IsRead }).ToList();
            List<int> triggered = state.TriggerLimitedQuest.ToList();
            List<PlayerDormQuestAccept> accepted = completed.Select(accept => state.QuestAccept.First(current => current.Index == accept.Index && current.ResetCount == accept.ResetCount)).ToList();
            foreach (PlayerDormQuestAccept accept in accepted)
            {
                QuestTable quest = quests[accept.QuestId];
                accept.IsAward = true;
                state.FinishQuestCount++;
                state.TerminalUpgradeExp++;
                RecordFinished(state, accept.QuestId);
                if (accept.IsSpecialQuest && !state.TriggerLimitedQuest.Contains(accept.QuestId)) state.TriggerLimitedQuest.Add(accept.QuestId);
                if (accept.FileId > 0 && state.CollectFiles.All(file => file.FileId != accept.FileId)) state.CollectFiles.Add(new PlayerDormCollectFile { FileId = accept.FileId });
            }
            session.player.Dorm.PendingRewards.AddRange(grants.Select(grant => new PlayerDormPendingReward { Key = grant.ClaimKey, Goods = grant.Goods.Select(good => new PlayerDormPendingRewardItem { Id = good.Id, TemplateId = good.TemplateId, Count = good.Count, Params = good.Params.ToList() }).ToList() }));
            try { session.player.SaveChecked(); }
            catch
            {
                foreach (PlayerDormQuestAccept accept in accepted) accept.IsAward = false;
                state.FinishQuestCount = finishQuestCount;
                state.TerminalUpgradeExp = terminalUpgradeExp;
                state.FinishQuests = finishQuests;
                state.CollectFiles = files;
                state.TriggerLimitedQuest = triggered;
                session.player.Dorm.PendingRewards.RemoveAll(pending => grants.Any(grant => grant.ClaimKey == pending.Key));
                session.SendResponse(new QuestGetAllRewardResponse { Code = QuestTerminalCfgError, DormQuestUpdate = Update(state) }, packet.Id);
                return;
            }
        }
        List<PlayerDormPendingReward> removed = [];
        try
        {
            RewardApplicationResult? rewards = grants.Count == 0 ? null : RewardHandler.ApplyRewardsOnceAndPersist(grants, session);
            removed = session.player.Dorm.PendingRewards.Where(pending => grants.Any(grant => grant.ClaimKey == pending.Key)).ToList();
            session.player.Dorm.PendingRewards.RemoveAll(pending => grants.Any(grant => grant.ClaimKey == pending.Key));
            session.player.SaveChecked();
            rewards?.SendPushes(session);
        }
        catch
        {
            session.player.Dorm.PendingRewards.AddRange(removed);
            session.SendResponse(new QuestGetAllRewardResponse { Code = QuestTerminalCfgError, DormQuestUpdate = Update(state) }, packet.Id);
            return;
        }
        session.SendResponse(new QuestGetAllRewardResponse
        {
            FinishQuestInfos = completed.Select(accept => new QuestFinishInfo { QuestId = accept.QuestId, TeamCharacter = accept.TeamCharacter.ToList(), FinishReward = quests[accept.QuestId].FinishReward, ExtraReward = accept.IsSatisfyRecommend ? quests[accept.QuestId].ExtraReward : 0, FileId = accept.FileId }).ToList(),
            DormQuestUpdate = Update(state)
        }, packet.Id);
    }

    [RequestPacketHandler("QuestRecallTeamRequest")]
    public static void QuestRecallTeamRequestHandler(Session session, Packet.Request packet)
    {
        QuestRecallTeamRequest request = packet.Deserialize<QuestRecallTeamRequest>();
        PlayerDormQuestState state = session.player.Dorm.Quest;
        PlayerDormQuestAccept? accept = state.QuestAccept.FirstOrDefault(quest => quest.Index == request.Index && quest.ResetCount == request.ResetCount && !quest.IsAward);
        int code = accept is null ? QuestNotAccept : 0;
        if (accept is not null)
        {
            state.QuestAccept.Remove(accept);
            session.player.SaveChecked();
            session.SendPush(new NotifyDormQuestData { TotalQuest = state.TotalQuest.Select(Quest).ToList(), QuestAccept = state.QuestAccept.Select(Accept).ToList() });
        }
        session.SendResponse(new QuestRecallTeamResponse { Code = code, QuestAccept = state.QuestAccept.Select(Accept).ToList() }, packet.Id);
    }

    [RequestPacketHandler("QuestReadFileRequest")]
    public static void QuestReadFileRequestHandler(Session session, Packet.Request packet)
    {
        QuestReadFileRequest request = packet.Deserialize<QuestReadFileRequest>();
        PlayerDormCollectFile? file = session.player.Dorm.Quest.CollectFiles.FirstOrDefault(file => file.FileId == request.FileId);
        int code = file is null ? QuestFileNotCollect : 0;
        if (file is not null && !file.IsRead) { file.IsRead = true; session.player.SaveChecked(); }
        session.SendResponse(new QuestReadFileResponse { Code = code }, packet.Id);
    }

    private static void NormalizeQuestState(Session session)
    {
        PlayerDormQuestState state = session.player.Dorm.Quest;
        bool changed = false;
        state.TerminalLv = Math.Max(1, state.TerminalLv);
        if (Terminal(state.TerminalLv) is null) { state.TerminalLv = 1; changed = true; }
        QuestTerminalTable? terminal = Terminal(state.TerminalLv);
        if (terminal is null) return;
        bool completedUpgrade = state.TerminalUpgradeStatus != 0 && terminal.NeedTime > 0 && QuestNow() >= state.TerminalUpgradeTime + terminal.NeedTime;
        if (completedUpgrade)
        {
            QuestTerminalTable? next = Terminal(state.TerminalLv + 1);
            state.TerminalUpgradeStatus = 0;
            state.TerminalUpgradeTime = 0;
            state.TerminalUpgradeExp = 0;
            if (next is not null) state.TerminalLv++;
            changed = true;
        }
        terminal = Terminal(state.TerminalLv)!;
        int expectedCount = BoardCount(session, terminal);
        bool invalid = state.TotalQuest.Count != expectedCount || state.TotalQuest.Select(quest => quest.QuestId).Distinct().Count() != state.TotalQuest.Count || state.TotalQuest.Any(quest => quest.ResetCount != state.ResetCount);
        if (invalid)
        {
            state.ResetCount++;
            state.TotalQuest = BuildBoard(session, terminal, state.ResetCount);
            changed = true;
        }
        if (changed) session.player.SaveChecked();
        if (completedUpgrade) session.SendPush(new NotifyDormQuestTerminalInit { TerminalLv = state.TerminalLv, TotalQuest = state.TotalQuest.Select(Quest).ToList() });
    }
    private static void RefillExhaustedBoard(Session session)
    {
        PlayerDormQuestState state = session.player.Dorm.Quest;
        if (state.TotalQuest.Count == 0 || state.TotalQuest.Any(board => !state.QuestAccept.Any(accept => accept.Index == board.Index && accept.ResetCount == board.ResetCount && accept.IsAward))) return;
        QuestTerminalTable? terminal = Terminal(state.TerminalLv);
        if (terminal is null) return;
        state.ResetCount++;
        state.TotalQuest = BuildBoard(session, terminal, state.ResetCount);
        session.player.SaveChecked();
    }


    private static List<PlayerDormQuest> BuildBoard(Session session, QuestTerminalTable terminal, int resetCount)
    {
        PlayerDormQuestState state = session.player.Dorm.Quest;
        Dictionary<int, QuestPoolTable> pools = TableReaderV2.Parse<QuestPoolTable>().ToDictionary(row => row.Id);
        Dictionary<int, QuestTable> quests = TableReaderV2.Parse<QuestTable>().ToDictionary(row => row.Id);
        List<int> poolIds = terminal.NormalQuest.Where(id => id > 0).ToList();
        List<int> minima = terminal.NormalQuestMin.ToList();
        List<int> weights = terminal.NormalQuestWeight.ToList();
        int normalCount = Math.Max(0, terminal.QuestCount - 1);
        List<int> selectedPools = [];
        for (int i = 0; i < poolIds.Count; i++) for (int count = 0; count < (i < minima.Count ? minima[i] : 0) && selectedPools.Count < normalCount; count++) selectedPools.Add(poolIds[i]);
        while (selectedPools.Count < normalCount)
        {
            int index = WeightedIndex(weights.Take(poolIds.Count).ToList(), session.player.PlayerData.Id, resetCount, selectedPools.Count);
            selectedPools.Add(poolIds[index]);
        }
        HashSet<int> used = [];
        List<PlayerDormQuest> result = [];
        for (int index = 0; index < selectedPools.Count; index++)
        {
            if (!pools.TryGetValue(selectedPools[index], out QuestPoolTable? pool)) continue;
            List<int> candidates = pool.QuestId.Where(id => quests.ContainsKey(id) && !used.Contains(id)).ToList();
            if (candidates.Count == 0) candidates = pools.Values.SelectMany(row => row.QuestId).Where(id => quests.ContainsKey(id) && !used.Contains(id)).Distinct().ToList();
            if (candidates.Count == 0) break;
            int questId = candidates[WeightedIndex(Weights(pool, candidates), session.player.PlayerData.Id, resetCount + selectedPools[index], index)];
            used.Add(questId);
            result.Add(new PlayerDormQuest { QuestId = questId, Index = index + 1, ResetCount = resetCount });
        }
        if (pools.TryGetValue(terminal.SpecialQuest, out QuestPoolTable? specialPool))
        {
            List<int> candidates = specialPool.QuestId.Where(id => quests.TryGetValue(id, out QuestTable? quest) && !state.TriggerLimitedQuest.Contains(id) && (quest.PreQuestId is not int pre || pre <= 0 || state.TriggerLimitedQuest.Contains(pre)) && ConditionsSatisfied(session, quest.Condition, quest, [])).ToList();
            if (candidates.Count > 0)
            {
                int questId = candidates[WeightedIndex(Weights(specialPool, candidates), session.player.PlayerData.Id, resetCount, normalCount)];
                result.Add(new PlayerDormQuest { QuestId = questId, Index = normalCount + 1, IsSpecialQuest = true, ResetCount = resetCount });
            }
        }
        return result;
    }

    private static int BoardCount(Session session, QuestTerminalTable terminal) => Math.Max(0, terminal.QuestCount - 1) + (HasSpecialQuest(session, terminal) ? 1 : 0);

    private static bool HasSpecialQuest(Session session, QuestTerminalTable terminal)
    {
        PlayerDormQuestState state = session.player.Dorm.Quest;
        Dictionary<int, QuestTable> quests = TableReaderV2.Parse<QuestTable>().ToDictionary(row => row.Id);
        QuestPoolTable? pool = TableReaderV2.Parse<QuestPoolTable>().FirstOrDefault(row => row.Id == terminal.SpecialQuest);
        return pool is not null && pool.QuestId.Any(id => quests.TryGetValue(id, out QuestTable? quest) && !state.TriggerLimitedQuest.Contains(id) && (quest.PreQuestId is not int pre || pre <= 0 || state.TriggerLimitedQuest.Contains(pre)) && ConditionsSatisfied(session, quest.Condition, quest, []));
    }

    private static int SelectFile(Session session, QuestTable quest, IEnumerable<uint> team, int resetCount, int index)
    {
        int poolId = quest.FilePoolId ?? 0;
        if (poolId <= 0) return 0;
        HashSet<int> owned = session.player.Dorm.Quest.CollectFiles.Select(file => file.FileId).ToHashSet();
        List<QuestFileTable> files = TableReaderV2.Parse<QuestFileTable>().Where(file => file.FilePoolId == poolId && !owned.Contains(file.Id) && (file.PreFileId is null || file.PreFileId == 0 || owned.Contains(file.PreFileId.Value)) && ConditionsSatisfied(session, file.Condition, quest, team)).ToList();
        return files.Count == 0 ? 0 : files[WeightedIndex(files.Select(file => file.Weight).ToList(), session.player.PlayerData.Id, resetCount + poolId, index)].Id;
    }

    private static int ValidateAccept(Session session, QuestAcceptRequest request)
    {
        PlayerDormQuestState state = session.player.Dorm.Quest;
        if (request.QuestAcceptParams.Count == 0 || request.QuestAcceptParams.Select(value => value.Index).Distinct().Count() != request.QuestAcceptParams.Count) return QuestIndexRepeat;
        if (state.QuestAccept.Count(accept => !accept.IsAward) + request.QuestAcceptParams.Count > (Terminal(state.TerminalLv)?.TeamCount ?? 0)) return QuestTerminalIdleCountNotEnough;
        HashSet<uint> owned = session.character.Characters.Select(character => character.Id).ToHashSet();
        HashSet<uint> busy = state.QuestAccept.Where(accept => !accept.IsAward).SelectMany(accept => accept.TeamCharacter).ToHashSet();
        Dictionary<int, QuestTable> quests = TableReaderV2.Parse<QuestTable>().ToDictionary(quest => quest.Id);
        foreach (QuestAcceptParam parameter in request.QuestAcceptParams)
        {
            PlayerDormQuest? board = state.TotalQuest.FirstOrDefault(quest => quest.Index == parameter.Index && quest.ResetCount == state.ResetCount);
            if (board is null) return QuestNotExist;
            if (state.QuestAccept.Any(accept => accept.Index == parameter.Index && accept.ResetCount == state.ResetCount)) return QuestAlreadyAccept;
            if (!quests.TryGetValue(board.QuestId, out QuestTable? quest)) return QuestCfgNotExist;
            if (parameter.TeamCharacter.Count != quest.MemberCount) return QuestTeamCharacterNotEnough;
            if (parameter.TeamCharacter.Distinct().Count() != parameter.TeamCharacter.Count) return QuestTeamCharacterRepeat;
            if (parameter.TeamCharacter.Any(id => !owned.Contains(id))) return QuestConditionNotFinish;
            if (parameter.TeamCharacter.Any(busy.Contains)) return QuestCharacterIsInQuest;
            foreach (uint id in parameter.TeamCharacter) busy.Add(id);
        }
        return 0;
    }

    private static bool IsRecommended(IEnumerable<uint> team, QuestTable quest)
    {
        HashSet<int> wanted = quest.RecommendAttrib.Where(attribute => attribute > 0).ToHashSet();
        if (wanted.Count == 0) return false;
        Dictionary<int, DormCharacterStyleTable> styles = TableReaderV2.Parse<DormCharacterStyleTable>().ToDictionary(style => style.Id);
        return team.Any(id => styles.TryGetValue((int)id, out DormCharacterStyleTable? style) && style.QuestAttrib.Any(wanted.Contains));
    }

    private static bool ConditionsSatisfied(Session session, IEnumerable<int> conditionIds, QuestTable? quest, IEnumerable<uint> team)
    {
        Dictionary<int, AscNet.Table.V2.share.condition.ConditionTable> conditions = TableReaderV2.Parse<AscNet.Table.V2.share.condition.ConditionTable>().ToDictionary(condition => condition.Id);
        return conditionIds.Where(id => id > 0).All(id => conditions.TryGetValue(id, out AscNet.Table.V2.share.condition.ConditionTable? condition) && condition.Type switch
        {
            20104 => condition.Params.Count > 0 && session.player.Dorm.Quest.TerminalLv >= condition.Params[0],
            20105 => condition.Params.Count > 1 && session.player.Dorm.Quest.FinishQuests.Sum(row => row.Count > 1 && TableReaderV2.Parse<QuestTable>().FirstOrDefault(quest => quest.Id == row[0])?.Type == condition.Params[0] ? row[1] : 0) >= condition.Params[1],
            34002 => quest is not null && IsRecommended(team, quest),
            34001 => condition.Params.Count > 0 && team.Contains((uint)condition.Params[0]),
            10105 => condition.Params.Count > 0 && condition.Params.All(stageId => session.stage.Stages.TryGetValue((uint)stageId, out StageDatum? stage) && stage.Passed),
            _ => false
        });
    }

    private static QuestTerminalTable? Terminal(int level) => TableReaderV2.Parse<QuestTerminalTable>().FirstOrDefault(row => row.Lv == level);
    private static uint QuestNow() => (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private static List<int> Weights(QuestPoolTable pool, List<int> ids) => ids.Select(id => { int position = pool.QuestId.IndexOf(id); return position >= 0 && position < pool.Weight.Count ? pool.Weight[position] : 0; }).ToList();
    private static int WeightedIndex(IReadOnlyList<int> weights, long playerId, int resetCount, int salt)
    {
        ulong seed = unchecked((ulong)playerId) ^ ((ulong)(uint)resetCount << 32) ^ (uint)salt;
        seed ^= seed >> 30; seed *= 0xbf58476d1ce4e5b9UL; seed ^= seed >> 27; seed *= 0x94d049bb133111ebUL; seed ^= seed >> 31;
        int total = weights.Sum(weight => Math.Max(0, weight));
        if (total <= 0) return (int)(seed % (uint)weights.Count);
        int roll = (int)(seed % (uint)total);
        for (int index = 0; index < weights.Count; index++) { roll -= Math.Max(0, weights[index]); if (roll < 0) return index; }
        return weights.Count - 1;
    }
    private static bool CanPay(Session session, QuestTerminalTable terminal) => terminal.NeedItem is not int item || item <= 0 || session.inventory.Items.FirstOrDefault(entry => entry.Id == item)?.Count >= (terminal.ItemCount ?? 0);
    private static NotifyItemDataList Pay(Session session, QuestTerminalTable terminal) { NotifyItemDataList result = new(); if (terminal.NeedItem is int item && item > 0 && terminal.ItemCount is int count && count > 0) result.ItemDataList.Add(session.inventory.Do(item, -count)); return result; }
    private static void AddGrant(List<RewardGrant> grants, string key, int rewardId) { List<AscNet.Table.V2.share.reward.RewardGoodsTable> goods = RewardHandler.GetRewardGoods(rewardId); if (goods.Count > 0) grants.Add(new RewardGrant(key, goods)); }
    private static string QuestClaimPrefix(Session session, PlayerDormQuestAccept accept) => $"dorm-quest:{session.player.PlayerData.Id}:{accept.ResetCount}:{accept.Index}:";
    private static IEnumerable<RewardGrant> Grants(Session session, PlayerDormQuestAccept accept, QuestTable quest)
    {
        List<RewardGrant> grants = [];
        AddGrant(grants, $"{QuestClaimPrefix(session, accept)}finish", quest.FinishReward);
        if (accept.IsSatisfyRecommend) AddGrant(grants, $"{QuestClaimPrefix(session, accept)}extra", quest.ExtraReward);
        return grants;
    }
    private static void RecordFinished(PlayerDormQuestState state, int questId) { List<int>? row = state.FinishQuests.FirstOrDefault(row => row.Count > 0 && row[0] == questId); if (row is null) state.FinishQuests.Add([questId, 1]); else { while (row.Count < 2) row.Add(0); row[1]++; } }
    private static NotifyDormitoryData.NotifyDormitoryDataDormQuest Quest(PlayerDormQuest value) => new() { QuestId = value.QuestId, FileId = value.FileId, Index = value.Index, IsSpecialQuest = value.IsSpecialQuest, ResetCount = value.ResetCount };
    private static NotifyDormitoryData.NotifyDormitoryDataDormQuestAccept Accept(PlayerDormQuestAccept value) => new() { QuestId = value.QuestId, AcceptTime = value.AcceptTime, TeamCharacter = value.TeamCharacter.ToList(), FileId = value.FileId, IsSpecialQuest = value.IsSpecialQuest, Index = value.Index, IsSatisfyRecommend = value.IsSatisfyRecommend, ResetCount = value.ResetCount, IsAward = value.IsAward };
    private static NotifyDormitoryData.NotifyDormitoryDataDormCollectFile File(PlayerDormCollectFile value) => new() { FileId = value.FileId, IsRead = value.IsRead };
    private static QuestDormQuestUpdate Update(PlayerDormQuestState value) => new() { QuestAccept = value.QuestAccept.Select(Accept).ToList(), CollectFiles = value.CollectFiles.Select(File).ToList(), TerminalUpgradeExp = value.TerminalUpgradeExp };
}
