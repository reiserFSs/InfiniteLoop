using AscNet.Common;
using AscNet.Common.MsgPack;
using AscNet.Table.V2.share.theatre4;

namespace AscNet.GameServer.Handlers;

// Awakening Tundra (Theatre4) choice surface: durable offer transactions (EN
// XTheatre4Transaction Type 1 Recruit / 2 Item / 3 Reward / 4 FightReward), the fight/drop
// confirmations that resolve them, the blueprint overflow replace/recycle pair, and the colour
// talent slot offers. Offers live in AdventureData.Transactions, so a retried packet or a relog
// replays the frozen roll and can never reroll or double-grant. Sampling rules with no authored
// server algorithm are labelled "AscNet Theatre4 local policy".
internal static partial class Theatre4Module
{
    //region errors

    private const int ChoiceError = 20218170;
    private const int ChoiceIndexError = 20218171;
    private const int ChoiceClaimedError = 20218172;
    private const int TalentError = 20218173;
    private const int TalentIndexError = 20218174;
    private const int TalentRefreshLimitError = 20218175;
    private const int WaitItemError = 20218176;

    private const int TransactionRecruit = 1, TransactionItem = 2, TransactionReward = 3,
        TransactionFightReward = 4;

    // EN XEnumConst.Theatre4.ItemRewardOperateType.
    private const int OperateAwards = 1, OperateRecycling = 2;

    //endregion

    //region transaction primitives

    private static readonly Lazy<Dictionary<int, Theatre4ColorTalentTable>> TalentRows = new(() =>
        Rows<Theatre4ColorTalentTable>().ToDictionary(row => row.Id));

    private static Theatre4TransactionData RequireTransaction(Mutation m, int transactionId, int type)
    {
        Theatre4AdventureData adventure = RequireAdventure(m);
        Theatre4TransactionData transaction = adventure.Transactions
            .FirstOrDefault(candidate => candidate.Id == transactionId && candidate.Type == type)
            ?? throw new ServerCodeException($"Theatre4 transaction {transactionId} is not active.", ChoiceError);
        return transaction;
    }

    // Every offer is created once and persisted; retries replay it instead of re-rolling.
    private static Theatre4TransactionData CreateTransaction(Mutation m, int type, int configId,
        List<Theatre4AssetData> rewards, int selectLimit, int refreshLimit = 0,
        List<Theatre4CharacterData>? characters = null)
    {
        Theatre4AdventureData adventure = RequireAdventure(m);
        int limit = Math.Max(1, selectLimit);
        Theatre4TransactionData transaction = new()
        {
            Id = NextId(m),
            Type = type,
            ConfigId = configId,
            SelectLimit = limit,
            SelectTimes = limit,
            RefreshLimit = Math.Max(0, refreshLimit),
            RefreshTimes = Math.Max(0, refreshLimit),
            Rewards = new(rewards),
            Characters = characters is null ? [] : new(characters)
        };
        adventure.Transactions.Add(transaction);
        m.Push(new NotifyTheatre4AddTransaction { Trx = Clone(transaction) });
        return transaction;
    }

    private static void RemoveTransaction(Mutation m, Theatre4TransactionData transaction)
    {
        RequireAdventure(m).Transactions.Remove(transaction);
        m.Push(new NotifyTheatre4RemoveTransaction { TrxId = transaction.Id });
    }

    private static void PushTransaction(Mutation m, Theatre4TransactionData transaction) =>
        m.Push(new NotifyTheatre4Transactions { Transactions = [Clone(transaction)] });

    // Shared claim step: index is 1-based into Transaction.Rewards and each index may be taken
    // once; the transaction retires when no picks remain.
    private static Theatre4AssetData ClaimReward(Mutation m, Theatre4TransactionData transaction, int index)
    {
        Require(index > 0 && index <= transaction.Rewards.Count, ChoiceIndexError);
        Require(!transaction.SelectIds.Contains(index), ChoiceClaimedError);
        Require(transaction.SelectTimes > 0, ChoiceError);
        Theatre4AssetData asset = transaction.Rewards[index - 1];
        transaction.SelectIds.Add(index);
        transaction.SelectTimes = Math.Max(0, transaction.SelectTimes - 1);
        return asset;
    }

    //endregion

    // XTheatre4Set uses Theatre4Character.Id as the recruit identity; CharacterId is the wire
    // field name. CharacterStar.ColorLevel is the authored cumulative multiplier preview.
    private static Dictionary<int, int> CharacterColorLevels(int characterId, int star)
    {
        Theatre4CharacterTable character = Rows<Theatre4CharacterTable>()
            .FirstOrDefault(row => row.Id == characterId)
            ?? throw new ServerCodeException($"Theatre4 character {characterId} is not authored.", AssetMissingError);
        Theatre4CharacterStarTable level = Rows<Theatre4CharacterStarTable>()
            .FirstOrDefault(row => row.GroupId == character.StarGroupId && (row.Star ?? 0) == star)
            ?? throw new ServerCodeException(
                $"Theatre4 character {characterId} has no authored star {star}.", AssetMissingError);
        Dictionary<int, int> result = [];
        for (int index = 0; index < Math.Min(3, level.ColorLevel.Count); index++)
            if (level.ColorLevel[index] != 0) result[index + 1] = level.ColorLevel[index];
        return result;
    }

    //region recruit

    [RequestPacketHandler("Theatre4RefreshRecruitRequest")]
    public static void Theatre4RefreshRecruit(Session session, Packet.Request packet) =>
        Handle<Theatre4RefreshRecruitRequest, Theatre4RefreshRecruitResponse>(session, packet, (m, request, response) =>
        {
            EnsureAvailable(session);
            Theatre4TransactionData transaction = RequireTransaction(m, request.TransactionId, TransactionRecruit);
            Require(transaction.RefreshTimes > 0, RecruitRefreshError);
            Theatre4RecruitTicketTable ticket = Rows<Theatre4RecruitTicketTable>()
                .FirstOrDefault(row => row.Id == transaction.ConfigId)
                ?? throw new ServerCodeException("Theatre4 recruit ticket is not authored.", AssetMissingError);
            transaction.RefreshTimes--;
            transaction.Characters = RollRecruitCharacters(m, ticket);
            transaction.SelectIds.Clear();
            PushTransaction(m, transaction);
            response.Transaction = Clone(transaction);
        });

    [RequestPacketHandler("Theatre4ConfirmRecruitRequest")]
    public static void Theatre4ConfirmRecruit(Session session, Packet.Request packet) =>
        Handle<Theatre4ConfirmRecruitRequest, Theatre4ConfirmRecruitResponse>(session, packet, (m, request, response) =>
        {
            EnsureAvailable(session);
            Theatre4AdventureData adventure = RequireAdventure(m);
            Theatre4TransactionData transaction = RequireTransaction(m, request.TransactionId, TransactionRecruit);
            Require(request.Index > 0 && request.Index <= transaction.Characters.Count, RecruitIndexError);
            Require(!transaction.SelectIds.Contains(request.Index), ChoiceClaimedError);
            Theatre4CharacterData offered = transaction.Characters[request.Index - 1];

            Theatre4CharacterData? owned = adventure.Characters
                .FirstOrDefault(character => character.CharacterId == offered.CharacterId);
            int beforeStar = owned?.Star ?? 0;
            int afterStar = Math.Max(beforeStar, offered.Star);
            bool isNew = owned is null;
            if (owned is null)
            {
                owned = new Theatre4CharacterData { CharacterId = offered.CharacterId, Star = afterStar };
                adventure.Characters.Add(owned);
            }
            else if (afterStar > owned.Star)
            {
                owned.Star = afterStar;
            }
            ApplyRecruitCharacterEffects(m, owned, CharacterColorLevels(offered.CharacterId, beforeStar),
                CharacterColorLevels(offered.CharacterId, afterStar), afterStar - beforeStar, isNew);
            m.Push(new NotifyTheatre4CharacterUpdate { Character = Clone(owned) });

            transaction.SelectIds.Add(request.Index);
            transaction.SelectTimes = Math.Max(0, transaction.SelectTimes - 1);
            // Character-star multipliers are applied by the shared effect helper; this trigger
            // remains unused because Type 7/9 target the recruited character, not a random colour.
            // EN share/task Condition 111004 counts reinforcement selections.
            RecordMetaProgress(m, "ReinforcementSelected");
            if (transaction.SelectTimes <= 0)
            {
                RemoveTransaction(m, transaction);
                // Next authored ticket opens its own offer; the client keeps one at a time.
                EnsureRecruitOffer(m);
                response.Transaction = null;
            }
            else
            {
                PushTransaction(m, transaction);
                response.Transaction = Clone(transaction);
            }
        });

    //endregion

    //region drops and fight rewards

    // Freezes a won fight's authored reward drop as a FightReward transaction. Type 1 keeps every
    // rolled reward as an individually confirmable card; Type 2 is a single-pick offer.
    // A wiped/settled run has no AdventureData left (the final boss ends it through
    // Map.AdvanceChapter), so the authored difficulty drop is the settlement reward instead.
    internal static Theatre4TransactionData? CreateFightRewardTransaction(Mutation m, int fightId)
    {
        if (m.Data.AdventureData is null) return null;
        Theatre4FightTable fight = Rows<Theatre4FightTable>().FirstOrDefault(row => row.Id == fightId)
            ?? throw new ServerCodeException($"Theatre4 fight {fightId} is not authored.", AssetMissingError);
        int dropId = fight.RewardDropId ?? 0;
        // Optional fight drop; event-chain rewards are resolved by the event itself.
        if (dropId == 0) return null;
        Require(dropId > 0, OfferError);
        return CreateFightRewardTransaction(m, fightId, dropId);
    }

    internal static Theatre4TransactionData? CreateFightRewardTransaction(Mutation m, int fightId, int dropId)
    {
        Theatre4AdventureData adventure = RequireAdventure(m);
        Theatre4TransactionData? existing = adventure.Transactions
            .FirstOrDefault(transaction => transaction.Type == TransactionFightReward && transaction.ConfigId == fightId);
        if (existing is not null) return existing;
        Theatre4RewardDropTable drop = Rows<Theatre4RewardDropTable>().FirstOrDefault(row => row.Id == dropId)
            ?? throw new ServerCodeException($"Theatre4 reward drop {dropId} is not authored.", AssetMissingError);
        List<int> groups = drop.GroupIds.Where(group => group > 0).ToList();
        Require(groups.Count > 0, AssetMissingError);
        List<Theatre4AssetData> rewards = RollDropOptions(m, drop, groups);
        // Every authored group condition-gated away = no reward for this fight; settle normally.
        if (rewards.Count == 0) return null;
        return CreateTransaction(m, TransactionFightReward, fightId, rewards,
            selectLimit: drop.Type == 2 ? 1 : rewards.Count);
    }

    [RequestPacketHandler("Theatre4ConfirmDropRequest")]
    public static void Theatre4ConfirmDrop(Session session, Packet.Request packet) =>
        Handle<Theatre4ConfirmDropRequest, Theatre4ConfirmDropResponse>(session, packet, (m, request, response) =>
        {
            EnsureAvailable(session);
            Theatre4TransactionData transaction = RequireTransaction(m, request.TransactionId, TransactionReward);
            Theatre4AssetData asset = ClaimReward(m, transaction, request.Index);
            GrantAsset(m, asset);
            m.Push(new NotifyTheatre4Reward { Rewards = [Clone(asset)] });
            if (transaction.SelectTimes <= 0) RemoveTransaction(m, transaction);
            else PushTransaction(m, transaction);
        });

    [RequestPacketHandler("Theatre4ConfirmFightRewardRequest")]
    public static void Theatre4ConfirmFightReward(Session session, Packet.Request packet) =>
        Handle<Theatre4ConfirmFightRewardRequest, Theatre4ConfirmFightRewardResponse>(session, packet, (m, request, response) =>
        {
            EnsureAvailable(session);
            Theatre4TransactionData transaction = RequireTransaction(m, request.TransactionId, TransactionFightReward);
            Theatre4AssetData asset = ClaimReward(m, transaction, request.Index);
            GrantAsset(m, asset);
            m.Push(new NotifyTheatre4Reward { Rewards = [Clone(asset)] });
            if (transaction.SelectTimes <= 0) RemoveTransaction(m, transaction);
            else PushTransaction(m, transaction);
        });

    [RequestPacketHandler("Theatre4QuitFightRewardRequest")]
    public static void Theatre4QuitFightReward(Session session, Packet.Request packet) =>
        Handle<Theatre4QuitFightRewardRequest, Theatre4QuitFightRewardResponse>(session, packet, (m, request, response) =>
        {
            EnsureAvailable(session);
            Theatre4TransactionData transaction = RequireTransaction(m, request.TransactionId, TransactionFightReward);
            // Abandoning forfeits every remaining card; nothing is granted.
            RemoveTransaction(m, transaction);
        });

    [RequestPacketHandler("Theatre4ConfirmItemRequest")]
    public static void Theatre4ConfirmItem(Session session, Packet.Request packet) =>
        Handle<Theatre4ConfirmItemRequest, Theatre4ConfirmItemResponse>(session, packet, (m, request, response) =>
        {
            EnsureAvailable(session);
            Theatre4AdventureData adventure = RequireAdventure(m);
            Theatre4TransactionData transaction = RequireTransaction(m, request.TransactionId, TransactionItem);
            Require(request.Index > 0 && request.Index <= transaction.Rewards.Count, ChoiceIndexError);
            Require(!transaction.SelectIds.Contains(request.Index), ChoiceClaimedError);
            Theatre4AssetData asset = transaction.Rewards[request.Index - 1];
            Require(asset.Type == T4Asset.Item, ItemError);

            if (request.OperateType == OperateAwards)
            {
                if (request.ReplaceItemUid > 0)
                {
                    // PopupChooseProp replace path: the owned blueprint makes way for the new one.
                    Theatre4ItemData owned = adventure.Items.FirstOrDefault(item => item.Uid == request.ReplaceItemUid)
                        ?? throw new ServerCodeException("Theatre4 replace target is not owned.", WaitItemError);
                    RemoveItemInstance(m, adventure, owned);
                }
                AddAsset(m, asset.Type, asset.Id, asset.Num);
            }
            else if (request.OperateType == OperateRecycling)
            {
                Theatre4ItemTable config = RequireItemConfig(asset.Id);
                int backPrice = Math.Max(0, config.BackPrice ?? 0);
                AddAsset(m, T4Asset.Gold, 0, backPrice);
            }
            else
            {
                throw new InvalidDataException($"Unsupported Theatre4 item operate type {request.OperateType}.");
            }

            transaction.SelectIds.Add(request.Index);
            transaction.SelectTimes = Math.Max(0, transaction.SelectTimes - 1);
            if (transaction.SelectTimes <= 0)
            {
                RemoveTransaction(m, transaction);
                // The box that produced this offer is retired from the owned ledger.
                if (adventure.ItemBoxs.Remove(transaction.ConfigId)) PushItemBoxs(m, adventure);
            }
            else
            {
                PushTransaction(m, transaction);
            }
        });

    //endregion

    //region blueprint overflow

    // PopupReplace "replace": a waiting blueprint swaps in for an owned one.
    [RequestPacketHandler("Theatre4ReplaceItemRequest")]
    public static void Theatre4ReplaceItem(Session session, Packet.Request packet) =>
        Handle<Theatre4ReplaceItemRequest, Theatre4ReplaceItemResponse>(session, packet, (m, request, response) =>
        {
            EnsureAvailable(session);
            Theatre4AdventureData adventure = RequireAdventure(m);
            Require(adventure.WaitItems.Contains(request.WaitItemId), WaitItemError);
            Theatre4ItemData owned = adventure.Items.FirstOrDefault(item => item.Uid == request.TargetItemUid)
                ?? throw new ServerCodeException("Theatre4 replace target is not owned.", WaitItemError);
            Require(RequireItemConfig(request.WaitItemId).IsProp is not > 0, ItemError);

            RemoveItemInstance(m, adventure, owned);
            adventure.WaitItems.Remove(request.WaitItemId);
            AddAsset(m, T4Asset.Item, request.WaitItemId, 1);
        });

    // PopupReplace "recycle": the waiting blueprint is sold for its authored back price.
    [RequestPacketHandler("Theatre4WaitItemRecyclingRequest")]
    public static void Theatre4WaitItemRecycling(Session session, Packet.Request packet) =>
        Handle<Theatre4WaitItemRecyclingRequest, Theatre4WaitItemRecyclingResponse>(session, packet, (m, request, response) =>
        {
            EnsureAvailable(session);
            Theatre4AdventureData adventure = RequireAdventure(m);
            Require(adventure.WaitItems.Contains(request.ItemId), WaitItemError);
            Theatre4ItemTable config = RequireItemConfig(request.ItemId);
            adventure.WaitItems.Remove(request.ItemId);
            AddAsset(m, T4Asset.Gold, 0, Math.Max(0, config.BackPrice ?? 0));
        });

    // Bag recycle: an owned blueprint is destroyed for its authored back price.
    [RequestPacketHandler("Theatre4ItemRecyclingRequest")]
    public static void Theatre4ItemRecycling(Session session, Packet.Request packet) =>
        Handle<Theatre4ItemRecyclingRequest, Theatre4ItemRecyclingResponse>(session, packet, (m, request, response) =>
        {
            EnsureAvailable(session);
            Theatre4AdventureData adventure = RequireAdventure(m);
            Theatre4ItemData item = adventure.Items.FirstOrDefault(candidate => candidate.Uid == request.Uid)
                ?? throw new ServerCodeException("Theatre4 item is not owned.", WaitItemError);
            Theatre4ItemTable config = RequireItemConfig(item.ItemId);
            Require(config.IsProp is not > 0, ItemError);
            RemoveItemInstance(m, adventure, item);
            AddAsset(m, T4Asset.Gold, 0, Math.Max(0, config.BackPrice ?? 0));
        });

    //endregion

    //region colour talents
    // Type413 (Battle Power Leap) is a capacity change, not a daily grant. The authored
    // effect names the colour in Params[0] and the total number of selections in Params[1].
    // The server evaluator is absent from the client source, so the numeric capacity and the
    // authored GenerateType/pool roles are an explicit AscNet Theatre4 local policy.
    internal static bool ApplySpecialTalentEffect(Mutation m, Theatre4EffectTable row)
    {
        return (row.Type ?? 0) switch
        {
            413 => ApplyBattlePowerLeap(m, row),
            415 => ApplyDivinePower(m, row),
            _ => false
        };
    }

    private static bool ApplyBattlePowerLeap(Mutation m, Theatre4EffectTable row)
    {
        int colorId = EffParam(row.Params, 0);
        Require(colorId is >= T4Color.Red and <= T4Color.Blue, TalentError);
        int capacity = EffParam(row.Params, 1);
        Require(capacity > 0, TalentError);

        Theatre4ColorTalentData color = RequireColor(m, colorId);
        // Opening the first pending slot is the observable activation when lower slots still
        // need the bonus picks. If every eligible slot is already complete, the capacity remains
        // active through the persisted Type413 effect and future EnsureTalentOffer calls.
        EnsureTalentOffer(m, color);
        return true;
    }

    private static bool IsBattlePowerLeapSlot(Theatre4ColorTalentSlotTable slot,
        Theatre4EffectTable effect)
    {
        if (slot.Color != EffParam(effect.Params, 0) || slot.GenerateType != 2) return false;

        // The final slot is excluded by its authored role: its pool contains the talent whose
        // effect group owns this Type413 row. This deliberately avoids a magic level/slot id.
        return !Rows<Theatre4ColorTalentPoolTable>()
            .Where(pool => pool.Group == slot.GeneratePoolGroup)
            .Any(pool => TalentRows.Value.TryGetValue(pool.TalentId, out Theatre4ColorTalentTable? talent)
                && EffectGroupEffects(talent.EffectGroupId).Contains(effect.Id));
    }

    private static Theatre4EffectTable? BattlePowerLeapEffect(Mutation m, int colorId) =>
        SnapshotEffects(m)
            .Select(effect => EffectConfig(effect.EffectId))
            .FirstOrDefault(row => row?.Type == 413 && EffParam(row.Params, 0) == colorId);

    private static int TalentSelectionCapacity(Mutation m, Theatre4ColorTalentSlotTable slot)
    {
        Theatre4EffectTable? effect = BattlePowerLeapEffect(m, slot.Color);
        if (effect is null || !IsBattlePowerLeapSlot(slot, effect)) return 1;
        int capacity = EffParam(effect.Params, 1);
        Require(capacity > 0, TalentError);
        return capacity;
    }

    // Type415 is an immediate current-map shop placement. Params[0] is the authored ShopId;
    // Params[1] is not an asset delta and must never debit BuildPoint.
    private static bool ApplyDivinePower(Mutation m, Theatre4EffectTable row)
    {
        bool applied = TryUnlockTalentShop(m, RequireAdventure(m), EffParam(row.Params, 0));
        Require(applied, TalentError);
        return true;
    }

    private static bool SlotNeedsTalentOffer(Mutation m, Theatre4ColorTalentData color,
        Theatre4ColorTalentSlotTable slot)
    {
        Theatre4ColorTalentSlotData? selected = color.Slots.FirstOrDefault(existing => existing.SlotId == slot.Id);
        int selectedCount = selected?.Talents.Count ?? 0;
        return selectedCount < TalentSelectionCapacity(m, slot);
    }


    private static Theatre4ActivityTable RequireActivity(Mutation m) =>
        Rows<Theatre4ActivityTable>().FirstOrDefault(row => row.Id == m.Data.ActivityId)
        ?? throw new ServerCodeException("Theatre4 activity is not authored.", AssetMissingError);

    private static int TalentRefreshLimit(Mutation m) => Math.Max(0, RequireActivity(m).TalentRefreshLimitNum);

    // Opens the next authored slot offer once the colour has banked enough talent points.
    // Returns true when a new offer was generated (the caller must publish the colour talent data,
    // which is the only notification carrying Slots/WaitSlot).
    private static bool EnsureTalentOffer(Mutation m, Theatre4ColorTalentData color)
    {
        if (color.WaitSlot is not null) return false;
        List<Theatre4ColorTalentSlotTable> slots = Rows<Theatre4ColorTalentSlotTable>()
            .Where(row => row.Color == color.Color && row.Level > 0 && row.UnlockPoint is > 0
                && color.Point >= row.UnlockPoint)
            .OrderBy(row => row.Level).ToList();
        Theatre4ColorTalentSlotTable? next = slots.FirstOrDefault(slot => SlotNeedsTalentOffer(m, color, slot));
        if (next is null) return false;
        int limit = TalentRefreshLimit(m);
        color.WaitSlot = new Theatre4ColorTalentWaitSlotData
        {
            SlotId = next.Id,
            TalentIds = RollTalents(m, color, next),
            RefreshFreeTimes = Math.Max(0, FreeTalentRefreshCount(m)),
            RefreshLimit = limit,
            RefreshTimes = limit
        };
        return true;
    }

    // Weighted draw from the authored talent pool group; already-owned talents are skipped unless
    // the pool row is marked Repeatable (AscNet Theatre4 local policy: no duplicates per offer).
    private static List<int> RollTalents(Mutation m, Theatre4ColorTalentData color, Theatre4ColorTalentSlotTable slot)
    {
        List<Theatre4ColorTalentPoolTable> rows = Rows<Theatre4ColorTalentPoolTable>()
            .Where(row => row.Group == slot.GeneratePoolGroup).ToList();
        Require(rows.Count > 0, TalentError);
        HashSet<int> owned = color.Slots.SelectMany(existing => existing.Talents)
            .Select(talent => talent.TalentId).ToHashSet();
        List<(int Id, int Weight)> pool = [];
        foreach (Theatre4ColorTalentPoolTable row in rows)
        {
            Require(TalentRows.Value.ContainsKey(row.TalentId), TalentError);
            if (owned.Contains(row.TalentId) && !(row.Repeatable is > 0)) continue;
            pool.Add((row.TalentId, Math.Max(0, row.Weight ?? 0)));
        }
        Require(pool.Count > 0, TalentError);
        int wanted = Math.Max(1, slot.GenerateNum);
        List<int> picked = [];
        while (picked.Count < wanted && pool.Count > 0)
        {
            long total = pool.Sum(candidate => (long)candidate.Weight);
            long ticket = total <= 0 ? RandomIndex(m, pool.Count) : RandomIndex(m, (int)Math.Min(int.MaxValue, total));
            int chosenIndex = pool.Count - 1;
            for (int index = 0; index < pool.Count; index++)
            {
                if (total <= 0 ? ticket-- == 0 : ticket < pool[index].Weight) { chosenIndex = index; break; }
                if (total > 0) ticket -= pool[index].Weight;
            }
            picked.Add(pool[chosenIndex].Id);
            pool.RemoveAt(chosenIndex);
        }
        return picked;
    }

    [RequestPacketHandler("Theatre4SelectTalentRequest")]
    public static void Theatre4SelectTalent(Session session, Packet.Request packet) =>
        Handle<Theatre4SelectTalentRequest, Theatre4SelectTalentResponse>(session, packet, (m, request, response) =>
        {
            EnsureAvailable(session);
            Theatre4AdventureData adventure = RequireAdventure(m);
            Theatre4ColorTalentData color = RequireColor(m, request.Color);
            Theatre4ColorTalentWaitSlotData wait = color.WaitSlot
                ?? throw new ServerCodeException("Theatre4 colour has no pending talent offer.", TalentError);
            Require(wait.TalentIds.Contains(request.TalentId), TalentIndexError);
            Theatre4ColorTalentTable config = TalentRows.Value.TryGetValue(request.TalentId, out Theatre4ColorTalentTable? row)
                ? row : throw new ServerCodeException("Theatre4 talent is not authored.", TalentError);

            Theatre4ColorTalentSlotTable slotConfig = Rows<Theatre4ColorTalentSlotTable>()
                .FirstOrDefault(row => row.Id == wait.SlotId && row.Color == color.Color)
                ?? throw new ServerCodeException("Theatre4 talent slot is not authored.", TalentError);
            Theatre4ColorTalentSlotData? slot = color.Slots.FirstOrDefault(existing => existing.SlotId == wait.SlotId);
            int selectedCount = slot?.Talents.Count ?? 0;
            Require(selectedCount < TalentSelectionCapacity(m, slotConfig), TalentError);
            Require(slot?.Talents.All(existing => existing.TalentId != request.TalentId) ?? true,
                TalentIndexError);
            if (slot is null)
            {
                slot = new Theatre4ColorTalentSlotData { SlotId = wait.SlotId };
                color.Slots.Add(slot);
            }
            Theatre4TalentData talent = new() { TalentId = request.TalentId };
            foreach (int effectId in EffectGroupEffects(config.EffectGroupId))
                talent.Effects.Add(new Theatre4EffectData { Id = NextId(m), EffectId = effectId });
            slot.Talents.Add(talent);

            color.WaitSlot = null;
            AddTalentAtlas(m, request.TalentId);
            RebuildEffects(m);
            m.Push(new NotifyTheatre4ColorTalentAddInfo { TalentIds = [request.TalentId] });
            EnsureTalentOffer(m, color);
            PushColor(m, color);
            m.Push(new NotifyTheatre4ColorTalentData { ColorTalents = adventure.Colors });
            response.ColorTalent = Clone(color);
        });

    [RequestPacketHandler("Theatre4RefreshTalentRequest")]
    public static void Theatre4RefreshTalent(Session session, Packet.Request packet) =>
        Handle<Theatre4RefreshTalentRequest, Theatre4RefreshTalentResponse>(session, packet, (m, request, response) =>
        {
            EnsureAvailable(session);
            Theatre4AdventureData adventure = RequireAdventure(m);
            Theatre4ColorTalentData color = RequireColor(m, request.Color);
            Theatre4ColorTalentWaitSlotData wait = color.WaitSlot
                ?? throw new ServerCodeException("Theatre4 colour has no pending talent offer.", TalentError);
            Require(wait.RefreshTimes > 0, TalentRefreshLimitError);
            int cost = TalentRefreshCost(m, wait);
            Require(adventure.Gold >= cost, ShopMoneyError);
            adventure.Gold -= cost;
            if (wait.RefreshFreeTimes > 0) wait.RefreshFreeTimes--;
            else wait.RefreshTimes--;
            Theatre4ColorTalentSlotTable slot = Rows<Theatre4ColorTalentSlotTable>()
                .FirstOrDefault(row => row.Id == wait.SlotId)
                ?? throw new ServerCodeException("Theatre4 talent slot is not authored.", TalentError);
            wait.TalentIds = RollTalents(m, color, slot);
            PushColor(m, color);
            m.Push(new NotifyTheatre4ColorTalentData { ColorTalents = adventure.Colors });
            PushResource(m);
        });

    // UiTheatre4PopupChooseGenius.GetColorTalentRefreshCost: free while free refreshes remain,
    // else the authored ladder indexed by the refresh ordinal, plus the effect surcharge.
    private static int TalentRefreshCost(Mutation m, Theatre4ColorTalentWaitSlotData wait)
    {
        if (wait.RefreshFreeTimes > 0) return 0;
        List<int> ladder = RequireActivity(m).TalentRefreshCost.Where(cost => cost >= 0).ToList();
        if (ladder.Count == 0) return 0;
        int ordinal = Math.Max(1, wait.RefreshLimit - wait.RefreshTimes + 1);
        int cost = ladder[Math.Min(ordinal, ladder.Count) - 1];
        if (cost <= 0) return 0;
        return Math.Max(0, cost + TalentRefreshExtraCost(m));
    }

    //endregion
}
