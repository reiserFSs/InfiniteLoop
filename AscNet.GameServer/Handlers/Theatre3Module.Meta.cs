using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.reward;
using AscNet.Table.V2.share.theatre3;
using System.Globalization;
using MessagePack;

namespace AscNet.GameServer.Handlers;

internal static partial class Theatre3Module
{
    private static List<T> MetaRows<T>() where T : ITable => TableReaderV2.Parse<T>();
    private static double MetaConfig(string key) => MetaRows<Theatre3ConfigTable>().Single(row => row.Key == key).Value;
    private static int MetaFloor(double value) => double.IsFinite(value) && value >= 0
        ? checked((int)Math.Floor(value)) : throw new InvalidDataException("Invalid Theatre3 progression factor.");

    internal static void InitializeMeta(Mutation m)
    {
        foreach (var item in MetaRows<Theatre3ItemTable>())
            if (IsConditionSatisfied(m, item.UnlockConditionId ?? 0) && !m.Data.UnlockItemId.Contains(item.Id))
                m.Data.UnlockItemId.Add(item.Id);
        foreach (var difficulty in MetaRows<Theatre3DifficultyTable>())
            if (IsConditionSatisfied(m, difficulty.ConditionId ?? 0) && !m.Data.UnlockDifficultyId.Contains(difficulty.Id))
                m.Data.UnlockDifficultyId.Add(difficulty.Id);
    }

    [RequestPacketHandler("Theatre3ActivationStrengthenTreeRequest")]
    public static void Theatre3ActivationStrengthenTreeRequestHandler(Session session, Packet.Request packet) =>
        Handle<Theatre3ActivationStrengthenTreeRequest, Theatre3ActivationStrengthenTreeResponse>(session, packet, (m, request, _) =>
        {
            var row = MetaRows<Theatre3StrengthenTreeTable>().SingleOrDefault(row => row.Id == request.Id);
            Require(row != null, 20203064);
            Require(!m.Data.UnlockStrengthTree.Contains(request.Id), 20203065);
            Require(row!.PreId.Where(id => id > 0).All(m.Data.UnlockStrengthTree.Contains)
                && IsConditionSatisfied(m, row.Condition ?? 0), 20203066);
            m.Cost(checked((int)MetaConfig("TalentTreePointItem")), row.NeedStrengthenPoint);
            m.Data.UnlockStrengthTree.Add(row.Id);
            if (m.Data.DifficultyId > 0) RecomputeCapacities(m);
            RecordProgress(m, 104018, m.Data.UnlockStrengthTree.Count);
        });

    [RequestPacketHandler("Theatre3GetBattlePassRewardRequest")]
    public static void Theatre3GetBattlePassRewardRequestHandler(Session session, Packet.Request packet) =>
        Handle<Theatre3GetBattlePassRewardRequest, Theatre3GetBattlePassRewardResponse>(session, packet, (m, request, response) =>
        {
            Require(request.GetRewardType is 1 or 2, 20203061);
            var eligible = EligibleBattlePass(m).ToList();
            if (request.GetRewardType == 1)
            {
                Require(MetaRows<Theatre3BattlePassTable>().Any(row => row.Level == request.Id), 20203061);
                Require(!m.Data.GetRewardIds.Contains(request.Id), 20203068);
                Require(eligible.Any(row => row.Level == request.Id), 20203062);
                eligible.RemoveAll(row => row.Level != request.Id);
            }
            else Require(eligible.Count > 0, 20203063);
            foreach (var row in eligible)
            {
                response.RewardGoodsList.AddRange(GrantMetaReward(m, row.RewardId));
                m.Data.GetRewardIds.Add(row.Level);
            }
            response.GetRewardIds = [.. m.Data.GetRewardIds];
        });

    private static IEnumerable<Theatre3BattlePassTable> EligibleBattlePass(Mutation m)
    {
        long required = 0;
        foreach (var row in MetaRows<Theatre3BattlePassTable>().OrderBy(row => row.Level))
        {
            required = checked(required + row.NeedExp);
            if (required > m.Data.TotalBattlePassExp) yield break;
            if (!m.Data.GetRewardIds.Contains(row.Level)) yield return row;
        }
    }

    [RequestPacketHandler("Theatre3GetAchievementRewardRequest")]
    public static void Theatre3GetAchievementRewardRequestHandler(Session session, Packet.Request packet) =>
        Handle<Theatre3GetAchievementRewardRequest, Theatre3GetAchievementRewardResponse>(session, packet, (m, request, response) =>
        {
            var activity = MetaRows<Theatre3ActivityTable>().Single(row => row.Id == m.Data.CurActivityId);
            int index = request.NeedCountId - 1;
            Require(index >= 0 && index < activity.NeedCounts.Count && index < activity.RewardIds.Count, 20203069);
            Require(!m.Data.AchievementRewards.Contains(request.NeedCountId), 20203068);
            Require(AchievementClaimCount(m) >= activity.NeedCounts[index], 20203069);
            response.RewardGoodsList = GrantMetaReward(m, activity.RewardIds[index]);
            m.Data.AchievementRewards.Add(request.NeedCountId);
        });

    private static List<RewardGoods> GrantMetaReward(Mutation m, int rewardId)
    {
        var goods = RewardHandler.GetRewardGoods(rewardId);
        if (goods.Count == 0) throw new InvalidDataException($"Missing Theatre3 reward {rewardId}.");
        m.Grant(rewardId);
        return goods.Select(row => new RewardGoods
        {
            Id = row.Id, TemplateId = row.TemplateId, Count = row.Count,
            RewardType = (int)(RewardHandler.GetRewardType(row) ?? throw new InvalidDataException($"Unsupported reward {row.Id}."))
        }).ToList();
    }

    [RequestPacketHandler("Theatre3SettleAdventureRequest")]
    public static void Theatre3SettleAdventureRequestHandler(Session session, Packet.Request packet) =>
        Handle<Theatre3SettleAdventureRequest, Theatre3SettleAdventureResponse>(session, packet, (m, _, response) =>
        {
            Require(m.Data.DifficultyId > 0, 20203002);
            response.SettleData = SettleRunCore(m, true, false);
        });

    internal static Theatre3SettleData SettleRun(Mutation m, bool abandoned = false) => SettleRunCore(m, abandoned, true);

    private static Theatre3SettleData SettleRunCore(Mutation m, bool abandoned, bool push)
    {
        if (m.Data.DifficultyId == 0 || m.State.SettledRunId == m.State.RunId && m.State.LastSettle != null)
        {
            Require(m.Data.DifficultyId == 0 && m.State.SettledRunId == m.State.RunId && m.State.LastSettle != null, 20203002);
            return RecoveredSettlement(m.State);
        }
        var ending = MetaRows<Theatre3EndingTable>().Where(row => row.PassType == (abandoned ? 1 : 2))
            .OrderByDescending(row => row.Priority).FirstOrDefault(row => EndingMatches(m, row))
            ?? throw new InvalidDataException("No eligible Theatre3 ending.");
        var difficulty = MetaRows<Theatre3DifficultyTable>().Single(row => row.Id == m.Data.DifficultyId);
        var factors = MetaRows<Theatre3SettleFactorTable>().ToDictionary(row => row.Id, row => row.Factor);
        var beforeItems = m.Data.UnlockItemId.ToHashSet();
        var result = new Theatre3SettleData
        {
            EndId = ending.Id, NodeCount = m.State.RunNodeCount, FightNodeCount = m.State.RunFightCount,
            ChapterCount = m.State.RunChapterCount, TotalItemCount = m.Data.Items.Count,
            TotalEquipCount = m.Data.Equips.Count, TotalSuitCount = CompleteSuitCount(m),
            EndFactor = ending.Factor.ToString("0.0###############", CultureInfo.InvariantCulture),
            Items = Clone(m.Data.Items), Equips = Clone(m.Data.Equips), EquipPos = Clone(m.Data.EquipPos),
            QubitValueA = m.Data.QubitValueA, QubitValueB = m.Data.QubitValueB,
            Characters = Clone(m.Data.Characters)
        };
        // Approved local rule: chapter factors are cumulative count bands, not summed rewards;
        // floor each score/currency once. No reward for an empty, pre-node abandoned run.
        if (result.NodeCount > 0)
        {
            result.NodeCountScore = MetaFloor(result.NodeCount * factors[1][0]);
            result.FightNodeCountScore = MetaFloor(result.FightNodeCount * factors[2][0]);
            result.ChapterCountScore = result.ChapterCount == 0 ? 0 : MetaFloor(factors[3][Math.Min(result.ChapterCount, factors[3].Count) - 1]);
            result.TotalSuitCountScore = MetaFloor(result.TotalSuitCount * factors[4][0]);
            result.TotalEquipCountScore = MetaFloor(result.TotalEquipCount * factors[5][0]);
            result.TotalItemCountScore = MetaFloor(result.TotalItemCount * factors[6][0]);
        }
        result.TotalScore = checked(result.NodeCountScore + result.FightNodeCountScore + result.ChapterCountScore
            + result.TotalSuitCountScore + result.TotalEquipCountScore + result.TotalItemCountScore);
        result.BPExp = MetaFloor(result.TotalScore * ending.Factor * difficulty.BPExpRate);
        m.Data.TotalBattlePassExp = checked(m.Data.TotalBattlePassExp + result.BPExp);
        GrantMetaCurrency(m, "BattlePassExpItem", result.BPExp);

        int characterExp = MetaFloor(result.NodeCount * MetaConfig("CharacterExpByNode") * difficulty.CharacterExpRate);
        foreach (var character in m.Data.Characters)
        {
            var wire = result.Characters.Single(row => row.CharacterId == character.CharacterId);
            wire.ExpTemp = m.State.RunCharacterIds.Contains(character.CharacterId) ? characterExp : 0;
            character.ExpTemp = 0;
            character.Exp = checked(character.Exp + wire.ExpTemp);
            foreach (var level in MetaRows<Theatre3CharacterLevelTable>()
                .Where(row => row.CharacterId == character.CharacterId && row.Level > character.Level).OrderBy(row => row.Level))
            {
                if (character.Exp < level.NeedExp) break;
                character.Exp -= level.NeedExp;
                character.Level = level.Level;
                result.StrengthPoint = checked(result.StrengthPoint + level.StrengthenPoint);
            }
            if (!abandoned && m.Data.EquipPos.Any(pos => pos.CardId == character.CharacterId
                || pos.RobotId > 0 && MetaRows<Theatre3CharacterRecruitTable>().Any(row => row.CharacterId == character.CharacterId && row.RobotId == pos.RobotId)))
                foreach (var record in MetaRows<Theatre3CharacterEndingTable>().Where(row => row.CharacterId == character.CharacterId && row.EndingId == ending.Id))
                    if (!character.EndingIds.Contains(record.Id)) character.EndingIds.Add(record.Id);
            wire.EndingIds = [.. character.EndingIds];
        }
        GrantMetaCurrency(m, "TalentTreePointItem", result.StrengthPoint);
        foreach (int chapter in m.State.CompletedChapterIds)
            if (!m.Data.PassChapterIds.Contains(chapter)) m.Data.PassChapterIds.Add(chapter);
        if (!abandoned)
        {
            m.Data.TotalAllPassCount = checked(m.Data.TotalAllPassCount + 1);
            m.Data.FirstPassFlag = Math.Max(m.Data.FirstPassFlag, difficulty.FirstPassFlag ?? 0);
            if (!m.Data.EndingRecord.Contains(ending.Id)) m.Data.EndingRecord.Add(ending.Id);
            if (!m.Data.PassDifficultyRecords.TryGetValue(difficulty.Id, out var records))
                m.Data.PassDifficultyRecords.Add(difficulty.Id, records = []);
            if (!records.Contains(ending.Id)) records.Add(ending.Id);
            RecordProgress(m, 104005, 1, ending.Id);
            RecordProgress(m, 104008);
        }
        RecordProgress(m, 104013, result.TotalSuitCount);
        RecordProgress(m, 104014, result.QubitValueA, 1);
        RecordProgress(m, 104014, result.QubitValueB, 2);
        int quantumLevel = MetaRows<Theatre3QubitLevelTable>().Where(row => row.Exp <= result.QubitValueA + result.QubitValueB)
            .Select(row => row.Level).DefaultIfEmpty().Max();
        RecordProgress(m, 104015, quantumLevel, 1);
        if (!abandoned && m.Data.DestinyCharacterId > 0 && m.Data.EquipPos.Any(pos => pos.CardId == m.Data.DestinyCharacterId
            || pos.RobotId > 0 && MetaRows<Theatre3CharacterRecruitTable>().Any(row => row.CharacterId == m.Data.DestinyCharacterId && row.RobotId == pos.RobotId)))
            RecordProgress(m, 104021);
        RecordProgress(m, 104010);
        // Approved local interpretation of the server-only destiny conversion: score / coefficient,
        // unlocked by the configured threshold of completed nodes, capped by the configured limit.
        int destiny = result.NodeCount >= MetaConfig("DestinyValueThreshold")
            ? MetaFloor(result.TotalScore / MetaConfig("DestinyValueCoefficient")) : 0;
        result.DestinyValue = Math.Max(0, Math.Min(destiny, checked((int)MetaConfig("DestinyValueMaxLimit")) - m.Data.DestinyValue));
        m.Data.DestinyValue = checked(m.Data.DestinyValue + result.DestinyValue);
        InitializeMeta(m);
        result.CurUnlockItemId = m.Data.UnlockItemId.Except(beforeItems).Concat(m.State.RunNewItemIds).Distinct().ToList();
        result.CurUnlockEquipId = [.. m.State.RunNewEquipIds];
        result.UnlockItemId = [.. m.Data.UnlockItemId]; result.UnlockEquipId = [.. m.Data.UnlockEquipId];
        result.UnlockDifficultyId = [.. m.Data.UnlockDifficultyId]; result.PassChapterIds = [.. m.Data.PassChapterIds];
        result.PassDifficultyRecords = Clone(m.Data.PassDifficultyRecords);
        result.FirstPassFlag = m.Data.FirstPassFlag; result.FireItemCount = m.Data.FireItemCount;
        m.State.LastSettle = Clone(result); m.State.SettledRunId = m.State.RunId; m.State.SettlementRecoveryPending = true;
        ResetSettledRun(m);
        m.Push(new NotifyTheatre3BattlePassExp { TotalBattlePassExp = m.Data.TotalBattlePassExp });
        if (push) m.Push(new NotifyTheatre3AdventureSettle { SettleData = Clone(result) });
        return result;
    }

    private static int CompleteSuitCount(Mutation m) => m.Data.Equips.GroupBy(equip => (equip.SuitId, equip.Pos)).Count(group =>
    {
        var suit = MetaRows<Theatre3EquipSuitTable>().Single(row => row.Id == group.Key.SuitId);
        int threshold = MetaRows<Theatre3EquipSuitEffectGroupTable>().Where(row => row.GroupId == suit.SuitEffectGroupId)
            .Max(row => row.ActivateLevel);
        return group.Select(equip => equip.EquipId).Distinct().Count() >= threshold;
    });

    private static bool EndingMatches(Mutation m, Theatre3EndingTable ending)
    {
        if (ending.Type.Count != ending.Param.Count) throw new InvalidDataException("Invalid Theatre3 ending requirement arity.");
        for (int i = 0; i < ending.Type.Count; i++)
        {
            // Authored FK domains: type3 references Chapter.Id, type4 references Stage.StageId.
            bool matches = ending.Type[i] switch
            {
                1 => ending.Param[i] == 0,
                2 => m.State.RunEventIds.Contains(ending.Param[i]),
                3 => m.State.CompletedChapterIds.Contains(ending.Param[i]),
                4 => m.Data.FightRecords.Any(record => record.StageId == ending.Param[i]),
                _ => false
            };
            if (!matches) return false;
        }
        return true;
    }

    private static void GrantMetaCurrency(Mutation m, string key, int count)
    {
        if (count <= 0) return;
        m.Grant(new RewardGrant($"theatre3:{m.Session.player.PlayerData.Id}:run:{m.State.RunId}:{key}",
            [new RewardGoodsTable { Id = 0, TemplateId = checked((int)MetaConfig(key)), Count = count }]));
    }

    private static void ResetSettledRun(Mutation m)
    {
        m.Cost(96189, checked((int)m.Balance(96189)), recordSpending: false);
        m.Data.DifficultyId = 0; m.Data.CurChapterId = 0; m.Data.CurChapterDb = null; m.Data.CurTeamData = null;
        m.Data.MaxEnergy = 0; m.Data.EquipPos.Clear(); m.Data.Items.Clear(); m.Data.Equips.Clear();
        m.Data.FightRecords.Clear(); m.Data.PassEventFightNodes.Clear(); m.Data.ChapterSwitch = false;
        m.Data.DestinyCharacterId = 0; m.Data.QubitValueA = 0; m.Data.QubitValueB = 0;
        m.State.Fight = null; m.State.PendingEndingId = 0;
    }

    internal static bool HasEndingRecord(Session session, int endingId) => session.player.Theatre3.Data.EndingRecord.Contains(endingId);

    internal static void ProjectSettlementRecovery(NotifyTheatre3ActivityData snapshot, PlayerTheatre3State state)
    {
        if (!state.SettlementRecoveryPending || state.LastSettle is not { } settle) return;
        ProjectSettlement(snapshot, settle);
    }

    internal static void ProjectSettlement(NotifyTheatre3ActivityData snapshot, Theatre3SettleData settle)
    {
        snapshot.DestinyValue = checked(snapshot.DestinyValue - settle.DestinyValue);
        if (MetaRows<Theatre3EndingTable>().Single(row => row.Id == settle.EndId).PassType == 2)
            snapshot.TotalAllPassCount = checked(snapshot.TotalAllPassCount - 1);
    }
    internal static byte[] PreparePacketReplay(Session session, string packetName, byte[] payload)
    {
        Theatre3SettleData? settle;
        object packet;
        if (packetName == nameof(NotifyTheatre3AdventureSettle))
        {
            var notification = MessagePackSerializer.Deserialize<NotifyTheatre3AdventureSettle>(payload);
            settle = notification.SettleData;
            packet = notification;
        }
        else if (packetName == nameof(Theatre3SettleAdventureResponse))
        {
            var response = MessagePackSerializer.Deserialize<Theatre3SettleAdventureResponse>(payload);
            if (response.Code != 0) return payload;
            settle = response.SettleData;
            packet = response;
        }
        else return payload;
        if (settle == null) return payload;
        var snapshot = MessagePackSerializer.Deserialize<NotifyTheatre3ActivityData>(
            MessagePackSerializer.Serialize(session.player.Theatre3.Data));
        ProjectSettlement(snapshot, settle);
        session.SendPush(snapshot);
        settle.Characters = Clone(session.player.Theatre3.Data.Characters);
        foreach (var character in settle.Characters) character.ExpTemp = 0;
        return MessagePackSerializer.Serialize(packet.GetType(), packet);
    }
    internal static void ReconcileRecoveredRequest(Session session, Theatre3PendingMutation pending)
    {
        // Owed incremental pushes were delivered first. Replace their result with the complete
        // durable state, including fields normally changed only by the lost request callback.
        session.SendPush(MessagePackSerializer.Deserialize<NotifyTheatre3ActivityData>(
            MessagePackSerializer.Serialize(session.player.Theatre3.Data)));
        if (pending.ResponseName != nameof(Theatre3SettleAdventureResponse) || pending.Response == null
            || pending.Pushes.Any(push => push.Name == nameof(NotifyTheatre3AdventureSettle))) return;
        var response = MessagePackSerializer.Deserialize<Theatre3SettleAdventureResponse>(pending.Response);
        if (response.Code != 0 || response.SettleData == null) return;
        var notification = new NotifyTheatre3AdventureSettle { SettleData = response.SettleData };
        session.SendPush(nameof(NotifyTheatre3AdventureSettle), PreparePacketReplay(session,
            nameof(NotifyTheatre3AdventureSettle), MessagePackSerializer.Serialize(notification)));
    }


    internal static Theatre3SettleData RecoveredSettlement(PlayerTheatre3State state)
    {
        var result = Clone(state.LastSettle ?? throw new InvalidOperationException("No Theatre3 settlement to recover."));
        // Login/retry does not necessarily play the mastery animation that consumes ExpTemp.
        // Keep the authoritative final roster rather than overwriting it with the old baseline.
        result.Characters = Clone(state.Data.Characters);
        foreach (var character in result.Characters) character.ExpTemp = 0;
        return result;
    }

    internal static void SendRecoveredSettlement(Session session)
    {
        if (session.player.Theatre3 is { SettlementRecoveryPending: true, LastSettle: not null } state)
            session.SendPush(new NotifyTheatre3AdventureSettle { SettleData = RecoveredSettlement(state) });
    }
}
