using AscNet.Common;
using AscNet.Common.MsgPack;
using AscNet.Table.V2.share.theatre4;

namespace AscNet.GameServer.Handlers;

// Awakening Tundra (Theatre4) economy: in-run assets, item/prop inventory, shops and recruit
// offers. Client authority is XTheatre4AssetSubControl.GetAssetCount (EN XEnumConst.Theatre4
// .AssetType), XTheatre4Adventure.GetItemCountById/GetItemLimit, XTheatre4Shop(-Goods) and the
// XTheatre4Transaction entity. Where no authored server algorithm exists (offer sampling, stock
// caps, box opening) the rule is labelled "AscNet Theatre4 local policy", never presented as
// retail parity.
internal static partial class Theatre4Module
{
    //region errors

    private const int AssetError = 20218150;          // malformed/unsupported asset request
    private const int AssetMissingError = 20218151;   // referenced authored row absent
    private const int AssetInsufficientError = 20218152;
    private const int ItemError = 20218153;
    private const int ItemLimitError = 20218154;
    private const int OfferError = 20218155;          // no live offer/transaction
    private const int OfferIndexError = 20218156;     // off-offer index
    private const int ShopError = 20218157;
    private const int ShopStockError = 20218158;
    private const int ShopMoneyError = 20218159;
    private const int ShopRefreshError = 20218160;
    private const int RecruitError = 20218161;
    private const int RecruitIndexError = 20218162;
    private const int RecruitRefreshError = 20218163;

    //endregion

    //region tables

    private static readonly Lazy<Dictionary<int, Theatre4ItemTable>> ItemRows = new(() =>
        Rows<Theatre4ItemTable>().ToDictionary(row => row.Id));

    private static readonly Lazy<Dictionary<int, Theatre4ItemBoxTable>> ItemBoxRows = new(() =>
        Rows<Theatre4ItemBoxTable>().ToDictionary(row => row.Id));

    private static readonly Lazy<Dictionary<int, Theatre4RewardTable>> RewardRows = new(() =>
        Rows<Theatre4RewardTable>().ToDictionary(row => row.Id));

    private static readonly Lazy<Dictionary<int, Theatre4ShopTable>> ShopRows = new(() =>
        Rows<Theatre4ShopTable>().ToDictionary(row => row.Id));

    private static Theatre4ItemTable RequireItemConfig(int itemId) =>
        ItemRows.Value.TryGetValue(itemId, out Theatre4ItemTable? row) ? row
        : throw new ServerCodeException($"Theatre4 item {itemId} is not authored.", AssetMissingError);

    private static Theatre4ShopTable RequireShopConfig(int shopId) =>
        ShopRows.Value.TryGetValue(shopId, out Theatre4ShopTable? row) ? row
        : throw new ServerCodeException($"Theatre4 shop {shopId} is not authored.", AssetMissingError);

    //endregion

    //region asset model

    // EN XTheatre4AssetSubControl.GetAssetCount: the exact field each AssetType reads.
    internal static int AssetCount(Mutation m, int type, int id)
    {
        if (m.Data.AdventureData is not { } adventure) return 0;
        switch (type)
        {
            case T4Asset.ItemBox: return adventure.ItemBoxs.Count(box => box == id);
            case T4Asset.Item: return adventure.Items.Concat(adventure.Props).Count(item => item.ItemId == id);
            case T4Asset.Recruit: return adventure.RecruitTickets.Count(ticket => ticket == id);
            case T4Asset.Gold: return adventure.Gold;
            case T4Asset.Hp: return adventure.Hp;
            case T4Asset.Prosperity: return adventure.Prosperity;
            case T4Asset.ColorLevel: return Color(adventure, id)?.Level ?? 0;
            case T4Asset.ColorResource: return Color(adventure, id)?.Resource ?? 0;
            case T4Asset.ColorPoint: return Color(adventure, id)?.Point ?? 0;
            case T4Asset.BuildPoint: return adventure.Bp;
            case T4Asset.ActionPoint: return adventure.Ap;
            case T4Asset.ColorDailyResource: return Color(adventure, id)?.DailyResource ?? 0;
            case T4Asset.ItemLimit: return adventure.ItemLimit;
            case T4Asset.SettleBpExp: return adventure.SettleBpExp;
            case T4Asset.AwakeningPoint: return adventure.AwakeningPoint;
            case T4Asset.ColorCostPoint: return Color(adventure, id)?.PointCanCost ?? 0;
            case T4Asset.TimeBack: return adventure.TracebackPoint;
            default: return 0;
        }
    }

    private static Theatre4ColorTalentData? Color(Theatre4AdventureData adventure, int color) =>
        color > 0 ? adventure.Colors.FirstOrDefault(candidate => candidate.Color == color) : null;

    internal static void AddAsset(Mutation m, int type, int id, int count)
    {
        Require(count >= 0, AssetError);
        if (count == 0) return;
        ApplyAsset(m, type, id, count);
    }

    // Throws (charging nothing) when the balance is short; callers that want a custom client code
    // pass their own insufficientCode.
    internal static void SpendAsset(Mutation m, int type, int id, int count, int insufficientCode = AssetError)
    {
        Require(count >= 0, AssetError);
        Require(AssetCount(m, type, id) >= count, insufficientCode);
        if (count <= 0) return;
        ApplyAsset(m, type, id, -count);
        // Type211 is per actual BuildPoint spent, not per build action. Trigger only after the
        // debit succeeds so rejected/zero-cost operations never grant its authored payout.
        if (type == T4Asset.BuildPoint) TriggerEffects(m, "build-point-spent", null, count);
    }

    private static void ApplyAsset(Mutation m, int type, int id, int delta)
    {
        Theatre4AdventureData adventure = RequireAdventure(m);
        switch (type)
        {
            case T4Asset.ItemBox:
                GrantItemBox(m, adventure, id, delta);
                return;
            case T4Asset.Item:
                GrantItems(m, adventure, id, delta);
                return;
            case T4Asset.Recruit:
                GrantRecruitTickets(m, adventure, id, delta);
                return;
            case T4Asset.Gold: adventure.Gold = Math.Max(0, checked(adventure.Gold + delta)); break;
            case T4Asset.Hp: adventure.Hp = Math.Max(0, checked(adventure.Hp + delta)); break;
            case T4Asset.Prosperity: adventure.Prosperity = Math.Max(0, checked(adventure.Prosperity + delta)); break;
            case T4Asset.BuildPoint: adventure.Bp = Math.Max(0, checked(adventure.Bp + delta)); break;
            case T4Asset.ActionPoint: adventure.Ap = Math.Max(0, checked(adventure.Ap + delta)); break;
            case T4Asset.ItemLimit: adventure.ItemLimit = Math.Max(0, checked(adventure.ItemLimit + delta)); break;
            case T4Asset.SettleBpExp: adventure.SettleBpExp = Math.Max(0, checked(adventure.SettleBpExp + delta)); break;
            case T4Asset.AwakeningPoint:
                adventure.AwakeningPoint = Math.Max(0, checked(adventure.AwakeningPoint + delta));
                break;
            case T4Asset.TimeBack:
                adventure.TracebackPoint = Math.Max(0, checked(adventure.TracebackPoint + delta));
                break;
            case T4Asset.ColorLevel:
            case T4Asset.ColorResource:
            case T4Asset.ColorPoint:
            case T4Asset.ColorDailyResource:
            case T4Asset.ColorCostPoint:
            {
                Theatre4ColorTalentData color = RequireColor(m, id);
                switch (type)
                {
                    case T4Asset.ColorLevel: color.Level = Math.Max(1, checked(color.Level + delta)); break;
                    case T4Asset.ColorResource: color.Resource = Math.Max(0, checked(color.Resource + delta)); break;
                    case T4Asset.ColorPoint: color.Point = Math.Max(0, checked(color.Point + delta)); break;
                    case T4Asset.ColorDailyResource:
                        color.DailyResource = Math.Max(0, checked(color.DailyResource + delta));
                        break;
                    default: color.PointCanCost = Math.Max(0, checked(color.PointCanCost + delta)); break;
                }
                if (type == T4Asset.ColorPoint && delta > 0)
                {
                    // Native UpdateColors treats this as a FULL replacement; publish every live colour.
                    // Talent points unlock authored offers; the stored colour multiplier is independent.
                    if (EnsureTalentOffer(m, color))
                        m.Push(new NotifyTheatre4ColorTalentData { ColorTalents = adventure.Colors });
                }
                PushColor(m, color);
                return;
            }
            default:
                throw new InvalidDataException($"Unsupported Theatre4 asset type {type}.");
        }
        PushResource(m);
    }

    internal static void PushResource(Mutation m)
    {
        Theatre4AdventureData adventure = RequireAdventure(m);
        m.Push(new NotifyTheatre4CustomResource
        {
            Gold = adventure.Gold, Hp = adventure.Hp, Ap = adventure.Ap, Bp = adventure.Bp,
            AwakeningPoint = adventure.AwakeningPoint, TracebackPoint = adventure.TracebackPoint,
            Prosperity = adventure.Prosperity, ItemLimit = adventure.ItemLimit
        });
    }

    internal static void PushColor(Mutation m, Theatre4ColorTalentData color) =>
        m.Push(new NotifyTheatre4ColorResourceData
        {
            Color = color.Color, Resource = color.Resource, Level = color.Level,
            DailyResource = color.DailyResource, Point = color.Point, PointCanCost = color.PointCanCost
        });

    // Crafted once per run start; Map has already seeded Hp/Ap/Gold/ItemLimit from the difficulty
    // row and the three colour rows. Economy only owns the counters it resets.
    internal static void InitializeEconomy(Mutation m)
    {
        Theatre4AdventureData adventure = RequireAdventure(m);
        adventure.EffectShopBuyTimes = 0;
        adventure.EffectSweepTimes = 0;
        adventure.ExtraMaxAp = Math.Max(0, adventure.ExtraMaxAp);
        adventure.ClashCount = Math.Max(0, adventure.ClashCount);
    }

    //endregion

    //region mode rewards

    // RewardId is a Theatre4Reward row (EN Theatre4Event.RewardId). ElementType is the AssetType,
    // ElementId the asset id (colour for colour assets) and ElementCount the amount.
    internal static void GrantModeReward(Mutation m, int rewardId)
    {
        if (!RewardRows.Value.TryGetValue(rewardId, out Theatre4RewardTable? row))
            throw new ServerCodeException($"Theatre4 reward {rewardId} is not authored.", AssetMissingError);
        if (row.Condition is > 0 && !IsConditionMet(m, row.Condition.Value)) return;
        Theatre4AssetData asset = new()
        {
            Type = row.ElementType, Id = row.ElementId ?? 0, Num = row.ElementCount, RewardId = row.Id
        };
        GrantAsset(m, asset);
        if (asset.Num > 0) m.Push(new NotifyTheatre4Reward { Rewards = [Clone(asset)] });
    }

    // RewardDrop.Type 1 = grant one authored reward row per group (probability-gated);
    // Type 2 = offer one rolled row per group as a single-pick Reward transaction.
    // An authored group whose rows are all condition-gated away yields no reward (never a
    // failure) so a fight/event drop can still settle.
    internal static void GrantModeDrop(Mutation m, int dropId)
    {
        Theatre4RewardDropTable row = Rows<Theatre4RewardDropTable>().FirstOrDefault(candidate => candidate.Id == dropId)
            ?? throw new ServerCodeException($"Theatre4 reward drop {dropId} is not authored.", AssetMissingError);
        List<int> groups = row.GroupIds.Where(group => group > 0).ToList();
        Require(groups.Count > 0, AssetMissingError);
        if (row.Type == 1)
        {
            List<Theatre4AssetData> granted = [];
            for (int index = 0; index < groups.Count; index++)
            {
                if (!Rolls(row, index, m)) continue;
                if (RollRewardGroup(m, groups[index]) is not { } asset) continue;
                GrantAsset(m, asset);
                granted.Add(asset);
            }
            if (granted.Count > 0) m.Push(new NotifyTheatre4Reward { Rewards = granted });
            return;
        }
        if (row.Type == 2)
        {
            List<Theatre4AssetData> options = RollDropOptions(m, row, groups);
            if (options.Count == 0) return;
            // Each option is one rolled authored row; the client confirms one index.
            CreateTransaction(m, 3, dropId, options, selectLimit: 1);
            return;
        }
        throw new InvalidDataException($"Unsupported Theatre4 reward drop type {row.Type}.");
    }

    // Shared by GrantModeDrop and the fight-reward freeze: one rolled row per surviving group.
    private static List<Theatre4AssetData> RollDropOptions(Mutation m, Theatre4RewardDropTable row, List<int> groups)
    {
        List<Theatre4AssetData> options = [];
        for (int index = 0; index < groups.Count; index++)
        {
            if (!Rolls(row, index, m)) continue;
            if (RollRewardGroup(m, groups[index]) is { } asset) options.Add(asset);
        }
        return options;
    }

    private static bool Rolls(Theatre4RewardDropTable row, int index, Mutation m)
    {
        if (row.Probabilitys.Count <= index) return true;
        double probability = row.Probabilitys[index];
        if (probability <= 0) return false;
        if (probability >= 1) return true;
        return RandomIndex(m, 10000) < (int)Math.Round(probability * 10000);
    }

    // One authored reward row out of a reward group, honouring Condition/ConditionWeight.
    // A missing group is a data defect; a group whose rows are all condition-gated away is an
    // authored "no reward" outcome and yields null.
    private static Theatre4AssetData? RollRewardGroup(Mutation m, int groupId)
    {
        List<Theatre4RewardTable> authored = Rows<Theatre4RewardTable>()
            .Where(row => row.GroupId == groupId).ToList();
        Require(authored.Count > 0, AssetMissingError);
        List<Theatre4RewardTable> rows = authored
            .Where(row => row.Condition is not > 0 || IsConditionMet(m, row.Condition.Value))
            .ToList();
        if (rows.Count == 0) return null;
        long total = rows.Sum(row => (long)Math.Max(0, Weight(row)));
        long ticket = total <= 0 ? RandomIndex(m, rows.Count) : RandomIndex(m, (int)Math.Min(int.MaxValue, total));
        foreach (Theatre4RewardTable row in rows)
        {
            long weight = Math.Max(0, Weight(row));
            if (total <= 0)
            {
                if (ticket-- == 0) return ToAsset(row);
                continue;
            }
            if (ticket < weight) return ToAsset(row);
            ticket -= weight;
        }
        return ToAsset(rows[^1]);
    }

    private static int Weight(Theatre4RewardTable row) => row.Weight
        + (row.Condition is > 0 && row.ConditionWeight is > 0 ? row.ConditionWeight.Value : 0);

    private static Theatre4AssetData ToAsset(Theatre4RewardTable row) => new()
    {
        Type = row.ElementType, Id = row.ElementId ?? 0, Num = row.ElementCount, RewardId = row.Id
    };

    // Applies one reward asset to the run, in the authored asset vocabulary.
    internal static void GrantAsset(Mutation m, Theatre4AssetData asset)
    {
        Require(asset.Type > 0 && asset.Num >= 0, AssetError);
        if (asset.Num == 0) return;
        AddAsset(m, asset.Type, asset.Id, asset.Num);
    }

    //endregion

    //region items and props

    // EN Theatre4Item.IsProp is the prop/blueprint split (XTheatre4Agency routes data.Item to
    // Props iff IsProp). Blueprints honour ItemLimit (AdventureData) and per-row CountLimit, and
    // overflow into WaitItems instead of vanishing.
    private static void GrantItems(Mutation m, Theatre4AdventureData adventure, int itemId, int delta)
    {
        Theatre4ItemTable config = RequireItemConfig(itemId);
        if (delta > 0)
        {
            for (int i = 0; i < delta; i++) AddItemInstance(m, adventure, config);
            return;
        }
        for (int i = 0; i < -delta; i++)
        {
            Theatre4ItemData? item = adventure.Items.Concat(adventure.Props)
                .LastOrDefault(candidate => candidate.ItemId == itemId);
            if (item is null) throw new ServerCodeException($"Theatre4 item {itemId} is not owned.", AssetInsufficientError);
            RemoveItemInstance(m, adventure, item);
        }
    }

    private static void AddItemInstance(Mutation m, Theatre4AdventureData adventure, Theatre4ItemTable config)
    {
        if (config.IsProp is > 0)
        {
            Theatre4ItemData prop = NewItemInstance(m, adventure, config);
            adventure.Props.Add(prop);
            AddItemEffects(m, prop);
            m.Push(new NotifyTheatre4ItemAdd { Item = Clone(prop) });
            RebuildEffects(m);
            return;
        }
        int countLimit = Math.Max(1, config.CountLimit);
        int owned = adventure.Items.Count(item => item.ItemId == config.Id);
        if (adventure.Items.Count < adventure.ItemLimit && owned < countLimit)
        {
            Theatre4ItemData item = NewItemInstance(m, adventure, config);
            adventure.Items.Add(item);
            AddItemEffects(m, item);
            AddItemAtlas(m, config.Id);
            m.Push(new NotifyTheatre4ItemAdd { Item = Clone(item) });
        }
        else
        {
            adventure.WaitItems.Add(config.Id);
            m.Push(new NotifyTheatre4ItemAdd { WaitItem = config.Id });
        }
        RebuildEffects(m);
    }

    private static Theatre4ItemData NewItemInstance(Mutation m, Theatre4AdventureData adventure, Theatre4ItemTable config) => new()
    {
        Uid = NextId(m),
        ItemId = config.Id,
        LeftDays = -1
    };

    private static void RemoveItemInstance(Mutation m, Theatre4AdventureData adventure, Theatre4ItemData item)
    {
        if (!adventure.Items.Remove(item) && !adventure.Props.Remove(item))
            throw new ServerCodeException("Theatre4 item is not owned.", AssetInsufficientError);
        RemoveEffectsOfItem(m, item.Uid);
        m.Push(new NotifyTheatre4RemoveItem { Item = Clone(item) });
        TriggerEffects(m, "itemremoved", null, 1);
        RebuildEffects(m);
    }

    internal static Theatre4ItemData? FindItem(Mutation m, int uid) =>
        m.Data.AdventureData is { } adventure
            ? adventure.Items.Concat(adventure.Props).FirstOrDefault(item => item.Uid == uid)
            : null;

    // A box opens into a single-pick blueprint offer. EN XTheatre4ItemBox.ItemGroupId lists the
    // groups; SafeItemGroupId is the fallback when every candidate is already at its count limit.
    private static void GrantItemBox(Mutation m, Theatre4AdventureData adventure, int boxId, int delta)
    {
        if (delta < 0)
        {
            for (int i = 0; i < -delta; i++)
            {
                if (!adventure.ItemBoxs.Remove(boxId))
                    throw new ServerCodeException($"Theatre4 item box {boxId} is not owned.", AssetInsufficientError);
            }
            PushItemBoxs(m, adventure);
            return;
        }
        Theatre4ItemBoxTable config = ItemBoxRows.Value.TryGetValue(boxId, out Theatre4ItemBoxTable? row) ? row
            : throw new ServerCodeException($"Theatre4 item box {boxId} is not authored.", AssetMissingError);
        for (int i = 0; i < delta; i++)
        {
            adventure.ItemBoxs.Add(boxId);
            OpenItemBox(m, adventure, config);
        }
        PushItemBoxs(m, adventure);
    }

    private static void OpenItemBox(Mutation m, Theatre4AdventureData adventure, Theatre4ItemBoxTable config)
    {
        List<Theatre4AssetData> options = [];
        HashSet<int> excluded = [];
        foreach (int groupId in config.ItemGroupId.Where(group => group > 0))
        {
            int itemId = RollItemGroup(m, adventure, groupId, excluded);
            if (itemId <= 0) continue;
            excluded.Add(itemId);
            options.Add(new Theatre4AssetData { Type = T4Asset.Item, Id = itemId, Num = 1 });
        }
        if (options.Count == 0 && config.SafeItemGroupId > 0)
        {
            int itemId = RollItemGroup(m, adventure, config.SafeItemGroupId, excluded);
            if (itemId > 0) options.Add(new Theatre4AssetData { Type = T4Asset.Item, Id = itemId, Num = 1 });
        }
        Require(options.Count > 0, AssetMissingError);
        // ConfigId carries the box so confirmation can retire the owned box.
        CreateTransaction(m, 2, config.Id, options, selectLimit: 1);
    }

    // Weighted authored item-group roll; items at CountLimit or outside the remaining limit are
    // skipped (local policy: prefer an ownable option, fall back to the safe group).
    private static int RollItemGroup(Mutation m, Theatre4AdventureData adventure, int groupId, HashSet<int> excluded)
    {
        List<Theatre4ItemGroupTable> rows = Rows<Theatre4ItemGroupTable>()
            .Where(row => row.GroupId == groupId
                && (row.Condition is not > 0 || IsConditionMet(m, row.Condition.Value)))
            .ToList();
        List<(int Id, int Weight)> candidates = [];
        foreach (Theatre4ItemGroupTable row in rows)
        {
            if (excluded.Contains(row.ItemId)) continue;
            Theatre4ItemTable config = RequireItemConfig(row.ItemId);
            if (config.IsProp is not > 0
                && adventure.Items.Count(item => item.ItemId == row.ItemId) >= Math.Max(1, config.CountLimit))
                continue;
            if (row.Weight > 0) candidates.Add((row.ItemId, row.Weight));
        }
        if (candidates.Count == 0) return 0;
        long total = candidates.Sum(candidate => (long)candidate.Weight);
        long ticket = RandomIndex(m, (int)Math.Min(int.MaxValue, total));
        foreach ((int id, int weight) in candidates)
        {
            if (ticket < weight) return id;
            ticket -= weight;
        }
        return candidates[^1].Id;
    }

    private static void PushItemBoxs(Mutation m, Theatre4AdventureData adventure) =>
        m.Push(new NotifyTheatre4ItemBoxs { ItemBoxs = new(adventure.ItemBoxs) });

    //endregion

    //region recruit tickets

    private static void GrantRecruitTickets(Mutation m, Theatre4AdventureData adventure, int ticketId, int delta)
    {
        if (delta < 0)
        {
            for (int i = 0; i < -delta; i++)
            {
                if (!adventure.RecruitTickets.Remove(ticketId))
                    throw new ServerCodeException($"Theatre4 recruit ticket {ticketId} is not owned.", AssetInsufficientError);
            }
        }
        else
        {
            for (int i = 0; i < delta; i++) adventure.RecruitTickets.Add(ticketId);
        }
        m.Push(new NotifyTheatre4RecruitTicks { RecruitTicks = new(adventure.RecruitTickets) });
        EnsureRecruitOffer(m);
    }

    // Creates the recruit-selection transaction for the next unspent ticket. The client keeps a
    // single Recruit transaction (XTheatre4SetControl errors on more than one).
    internal static void EnsureRecruitOffer(Mutation m)
    {
        Theatre4AdventureData adventure = RequireAdventure(m);
        if (adventure.Transactions.Any(transaction => transaction.Type == 1)) return;
        int ticketId = adventure.RecruitTickets.FirstOrDefault(id => id > 0);
        if (ticketId <= 0) return;
        Theatre4RecruitTicketTable ticket = Rows<Theatre4RecruitTicketTable>()
            .FirstOrDefault(row => row.Id == ticketId)
            ?? throw new ServerCodeException($"Theatre4 recruit ticket {ticketId} is not authored.", AssetMissingError);
        Require(ticket.RefreshLimit >= 0, AssetMissingError);
        adventure.RecruitTickets.Remove(ticketId);
        CreateTransaction(m, 1, ticketId, [], selectLimit: Math.Max(1, ticket.SelectNum),
            refreshLimit: Math.Max(0, ticket.RefreshLimit), characters: RollRecruitCharacters(m, ticket));
        m.Push(new NotifyTheatre4RecruitTicks { RecruitTicks = new(adventure.RecruitTickets) });
    }

    // Called by Map on SelectAffix once the affix has deposited its authored tickets.
    internal static void BeginAdventureRecruit(Mutation m) => EnsureRecruitOffer(m);

    private static List<Theatre4CharacterData> RollRecruitCharacters(Mutation m, Theatre4RecruitTicketTable ticket)
    {
        List<Theatre4CharacterGroupTable> rows = Rows<Theatre4CharacterGroupTable>()
            .Where(row => row.GroupId == ticket.CharacterGroupA).ToList();
        Require(rows.Count > 0, AssetMissingError);
        int wanted = Math.Max(1, ticket.CharacterNumA);
        List<Theatre4CharacterData> characters = [];
        List<Theatre4CharacterGroupTable> pool = new(rows);
        while (characters.Count < wanted && pool.Count > 0)
        {
            Theatre4CharacterGroupTable? chosen = PickWeighted(m, pool, row => row.Weight
                + row.AddWeight.Select((weight, index) => row.AddWeightCondition.Count > index
                    && row.AddWeightCondition[index] is > 0 && IsConditionMet(m, row.AddWeightCondition[index])
                    ? weight : 0).Sum());
            if (chosen is null) break;
            pool.Remove(chosen);
            int star = chosen.Star ?? 0;
            characters.Add(new Theatre4CharacterData
            {
                CharacterId = chosen.CharacterId, Star = star,
                ColorLevelAdds = CharacterColorLevels(chosen.CharacterId, star)
            });
        }
        Require(characters.Count > 0, AssetMissingError);
        return characters;
    }

    //endregion

    //region shops

    // Wire Stock is independent remaining buys (client IsSoldOut: stock <= 0); GoodsNum is the
    // per-purchase quantity. No authored stock column exists, so AscNet Theatre4 local policy
    // gives every shelf entry a single purchase.
    internal static int ShopStock(Mutation m, int goodsId) => 1;

    // Rebuilds one shop's shelf from its authored goods groups, applying effect-driven free
    // purchases, the random free column and the effect discount offset.
    internal static void RefreshShopShelf(Mutation m, Theatre4ShopData shop)
    {
        Theatre4ShopTable config = RequireShopConfig(shop.ShopId);
        shop.Goods.Clear();
        shop.Goods.AddRange(BuildShopGoods(m, config.GoodsGroupId));
        foreach (Theatre4ShopGoodsData goods in shop.Goods) goods.Stock = ShopStock(m, goods.GoodsId);
        shop.Discount = ShopDiscountRatio(m);
        shop.FreeBuyTimes = Math.Max(0, FirstPurchaseFree(m) ? Math.Max(1, EffectSimpleValue(m, 202)) : 0);
        if (RandomFreeShopSlot(m) && shop.Goods.Count > 0)
            shop.Goods[RandomIndex(m, shop.Goods.Count)].IsFree = true;
    }

    internal static void FindShopGrid(Mutation m, int mapId, int posX, int posY,
        out Theatre4GridData grid, out Theatre4ShopData shop)
    {
        grid = FindGrid(m, mapId, posX, posY);
        Require(grid.Type == T4Grid.Shop, ShopError);
        shop = grid.Shop ?? throw new ServerCodeException("Theatre4 grid has no shop.", ShopError);
        // Effect-created shops (Type201) arrive with an empty shelf; hydrate it on first use.
        if (shop.Goods.Count == 0) RefreshShopShelf(m, shop);
    }

    // Client GetDiscountPrice: free when FreeBuyTimes > 0, otherwise floor(price * (10000+offset)/10000).
    internal static int ShopPrice(Theatre4ShopData shop, Theatre4ShopGoodsData goods, int basePrice)
    {
        if (goods.IsFree) return 0;
        int discount = Math.Clamp(basePrice, 0, int.MaxValue);
        int offset = Math.Max(-10000, shop.Discount);
        return (int)((long)discount * (10000 + offset) / 10000);
    }

    //endregion

    //region shop requests

    [RequestPacketHandler("Theatre4ShopBuyRequest")]
    public static void Theatre4ShopBuy(Session session, Packet.Request packet) =>
        Handle<Theatre4ShopBuyRequest, Theatre4ShopBuyResponse>(session, packet, (m, request, response) =>
        {
            EnsureAvailable(session);
            FindShopGrid(m, request.MapId, request.PosX, request.PosY, out Theatre4GridData grid, out Theatre4ShopData shop);
            Theatre4AdventureData adventure = RequireAdventure(m);
            Require(request.GoodsIndex > 0 && request.GoodsIndex <= shop.Goods.Count, ShopError);
            Theatre4ShopGoodsData goods = shop.Goods[request.GoodsIndex - 1];
            Require(goods.Stock > 0, ShopStockError);
            Theatre4ShopGoodsTable row = Rows<Theatre4ShopGoodsTable>().FirstOrDefault(candidate => candidate.Id == goods.GoodsId)
                ?? throw new ServerCodeException($"Theatre4 shop goods {goods.GoodsId} is not authored.", AssetMissingError);

            int price = shop.FreeBuyTimes > 0 ? 0 : ShopPrice(shop, goods, row.Price);
            Require(adventure.Gold >= price, ShopMoneyError);
            if (shop.FreeBuyTimes > 0) shop.FreeBuyTimes--;
            else adventure.Gold -= price;

            goods.Stock = Math.Max(0, goods.Stock - 1);
            // EN Theatre4Item 26: the random free column disappears after any purchase.
            foreach (Theatre4ShopGoodsData candidate in shop.Goods) candidate.IsFree = false;
            int quantity = Math.Max(1, row.GoodsNum);
            GrantAsset(m, new Theatre4AssetData { Type = row.GoodsType, Id = row.GoodsId ?? 0, Num = quantity });

            adventure.EffectShopBuyTimes = checked(adventure.EffectShopBuyTimes + 1);
            m.Push(new NotifyTheatre4CustomCounter
            {
                EffectShopBuyTimes = adventure.EffectShopBuyTimes, EffectSweepTimes = adventure.EffectSweepTimes
            });
            TriggerEffects(m, "shop", grid, 1);
            PushShopGrid(m, request.MapId, grid, shop);
            response.Shop = Clone(shop);
            PushResource(m);
        });

    [RequestPacketHandler("Theatre4RefreshGoodsRequest")]
    public static void Theatre4RefreshGoods(Session session, Packet.Request packet) =>
        Handle<Theatre4RefreshGoodsRequest, Theatre4RefreshGoodsResponse>(session, packet, (m, request, response) =>
        {
            EnsureAvailable(session);
            FindShopGrid(m, request.MapId, request.PosX, request.PosY, out Theatre4GridData grid, out Theatre4ShopData shop);
            Theatre4AdventureData adventure = RequireAdventure(m);
            Theatre4ShopTable config = RequireShopConfig(shop.ShopId);
            Require(shop.RefreshTimes < config.RefreshLimit, ShopRefreshError);
            int cost = ShopRefreshCost(shop.RefreshTimes, config);
            Require(adventure.Gold >= cost, ShopMoneyError);
            adventure.Gold -= cost;
            shop.RefreshTimes = checked(shop.RefreshTimes + 1);
            RefreshShopShelf(m, shop);
            // Refreshing an already-entered shop is not an entry: Type217 is fired once by
            // OpenGridShop on first explore (or by Effects when it creates a shop).
            PushShopGrid(m, request.MapId, grid, shop);
            response.Shop = Clone(shop);
            PushResource(m);
        });

    // UiTheatre4Shop.GetShopRefreshCost: first RefreshFreeTimes refreshes are free, then the
    // authored RefreshCost ladder is indexed by the refresh ordinal.
    private static int ShopRefreshCost(int refreshTimes, Theatre4ShopTable config)
    {
        List<int> costs = config.RefreshCost.Where(cost => cost >= 0).ToList();
        if (costs.Count == 0 || refreshTimes < config.RefreshFreeTimes) return 0;
        int index = Math.Min(costs.Count - 1, Math.Max(0, refreshTimes - config.RefreshFreeTimes));
        return costs[index];
    }

    // Called by Map when a shop grid is created, so the authored shelf, stock, discount offset and
    // effect-driven free buys/free column are all established before the first purchase.
    internal static void InitializeShop(Mutation m, Theatre4ShopData shop) => RefreshShopShelf(m, shop);

    internal static void PushShopGrid(Mutation m, int mapId, Theatre4GridData grid, Theatre4ShopData shop) =>
        m.Push(new NotifyTheatre4ChangeGrids { MapId = mapId, Grids = [Clone(grid)] });

    //endregion

    //region grid content

    // Shop entry (EN effect Type217): fires once on the first exploration of a shop tile, the only
    // server-observable entry. Generating a shelf or refreshing an entered shop must NOT fire it.
    internal static void OpenGridShop(Mutation m, Theatre4GridData grid)
    {
        Require(grid.Type == T4Grid.Shop && grid.Shop is not null, ShopError);
        TriggerEffects(m, "shop", grid, 0);
    }

    // Chest grid (T4Grid.Box): ContentId freezes the selected authored BoxGroup row; its DropId
    // then enters the existing guarded reward-drop transaction.
    internal static void OpenGridBox(Mutation m, Theatre4GridData grid)
    {
        Require(grid.Type == T4Grid.Box, AssetError);
        Theatre4BoxGroupTable row = Rows<Theatre4BoxGroupTable>()
            .FirstOrDefault(candidate => candidate.Id == grid.ContentId && candidate.GroupId == grid.ContentGroup)
            ?? throw new ServerCodeException($"Theatre4 box {grid.ContentId} is not authored for group {grid.ContentGroup}.", AssetMissingError);
        grid.State = T4State.Processed;
        GrantModeDrop(m, row.DropId);
    }

    //endregion
}
