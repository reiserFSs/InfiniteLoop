using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.Table.V2.share.theatre4;
using AscNet.Table.V2.share.theatre4.theatre4map;
using MessagePack;
using Newtonsoft.Json.Linq;

namespace AscNet.Test;

internal partial class Program
{
    private const int T4EffectAuditAssetRecruit = 3;

    // Source authority: EN XTheatre4EffectSubControl (Type 1/2/3/19/32/33/38),
    // XTheatre4Adventure's daily fields, XTheatre4MapSubControl's coordinate/range order,
    // Theatre4Effect/RecruitTicket/CharacterStar authored rows, and the registered handlers.
    // Straight/corner is the explicit AscNet local policy because the shipped client has no
    // server-side evaluator: three same-map cells form a line or a right angle.
    private static void ValidateTheatre4EffectAuditCompatibility()
    {
        ValidateTheatre4EffectAuditBountyAndAdjacency();
        ValidateTheatre4EffectAuditDailyColors();
        ValidateTheatre4EffectAuditPersistentCounters();
        ValidateTheatre4EffectAuditShopPurchase();
        ValidateTheatre4EffectAuditRecruitment();
        ValidateTheatre4EffectAuditMapShapes();
    }

    private static void ValidateTheatre4EffectAuditBountyAndAdjacency()
    {
        Theatre4EffectTable bounty = T4EffectAuditEffectByType(10);
        Theatre4EffectTable distance = T4EffectAuditEffectByType(3);

        using (Theatre4Case bountyCase = new("effect-audit-bounty"))
        {
            bountyCase.StartRun(1);
            Theatre4SeedEffect(bountyCase, bounty.Id);
            bountyCase.Adventure.Ap = 100;
            int mapId = bountyCase.Adventure.Chapters[0].MapId;
            Theatre4GridData target = bountyCase.Adventure.Chapters[0].Grids
                .FirstOrDefault(grid => grid.PosX != grid.PosY
                    && grid.Type is T4GridMonster or T4GridBoss && grid.ColorResource > 0)
                ?? throw new InvalidDataException("Type10 fixture has no authored coloured enemy tile.");
            target.State = T4StateDiscover;
            bountyCase.SaveFixture();

            int color = target.Color;
            int baseResource = target.ColorResource;
            int colorResourceBefore = bountyCase.Adventure.Colors.Single(item => item.Color == color).Resource;
            JObject response = bountyCase.Call(nameof(Theatre4ExploreGridRequest),
                new Theatre4ExploreGridRequest { MapId = mapId, PosX = target.PosX, PosY = target.PosY });
            JObject responseGrid = RequiredObject(response, "Grid", "Type10 bounty response");
            AssertEqual(baseResource * 2,
                RequiredValue<int>(responseGrid, "ColorResource", JTokenType.Integer, "Type10 bounty response"),
                "Type10 doubles the selected enemy tile in the response");
            AssertEqual(baseResource * 2, bountyCase.Grid(mapId, target.PosX, target.PosY).ColorResource,
                "Type10 persists the doubled enemy tile");
            AssertEqual(colorResourceBefore + baseResource * 2,
                bountyCase.Adventure.Colors.Single(item => item.Color == color).Resource,
                "Type10 credits the doubled tile resource exactly once");
        }

        using (Theatre4Case distanceCase = new("effect-audit-distance"))
        {
            distanceCase.StartRun(1);
            Theatre4SeedEffect(distanceCase, distance.Id);
            distanceCase.Adventure.Ap = 100;
            Theatre4ChapterData chapter = distanceCase.Adventure.Chapters[0];
            List<Theatre4GridData> candidates = chapter.Grids
                .Where(grid => grid.PosX != grid.PosY && grid.Type != T4GridStart).ToList();
            Theatre4GridData? first = null;
            Theatre4GridData? adjacent = null;
            Theatre4GridData? distant = null;
            foreach (Theatre4GridData candidate in candidates)
            {
                Theatre4GridData? next = candidates.FirstOrDefault(other => other != candidate
                    && Math.Abs(other.PosX - candidate.PosX) <= 1
                    && Math.Abs(other.PosY - candidate.PosY) <= 1);
                Theatre4GridData? far = next is null ? null : candidates.FirstOrDefault(other => other != candidate
                    && other != next
                    && (Math.Abs(other.PosX - candidate.PosX) > 1
                        || Math.Abs(other.PosY - candidate.PosY) > 1)
                    && (Math.Abs(other.PosX - next.PosX) > 1
                        || Math.Abs(other.PosY - next.PosY) > 1));
                if (next is not null && far is not null)
                {
                    first = candidate;
                    adjacent = next;
                    distant = far;
                    break;
                }
            }
            if (first is null || adjacent is null || distant is null)
                throw new InvalidDataException("Type3 fixture has no adjacent and distant authored positions.");
            foreach (Theatre4GridData grid in new[] { first, adjacent, distant })
                T4EffectAuditPrepareExploreTile(grid, color: 1);
            distanceCase.Adventure.PreExplorePos = null;
            distanceCase.SaveFixture();

            int bonus = distance.Params.FirstOrDefault();
            int before = distanceCase.Adventure.Colors.Single(color => color.Color == 1).Resource;
            distanceCase.Call(nameof(Theatre4ExploreGridRequest), new Theatre4ExploreGridRequest
            {
                MapId = chapter.MapId, PosX = first.PosX, PosY = first.PosY
            });
            AssertEqual(before + bonus, distanceCase.Adventure.Colors.Single(color => color.Color == 1).Resource,
                "Type3 grants the first tile when no previous exploration exists");
            AssertEqual(first.PosX, distanceCase.Adventure.PreExplorePos!.PosX,
                "Type3 stores the first explored X as the previous position");

            before = distanceCase.Adventure.Colors.Single(color => color.Color == 1).Resource;
            distanceCase.Call(nameof(Theatre4ExploreGridRequest), new Theatre4ExploreGridRequest
            {
                MapId = chapter.MapId, PosX = adjacent.PosX, PosY = adjacent.PosY
            });
            AssertEqual(before, distanceCase.Adventure.Colors.Single(color => color.Color == 1).Resource,
                "Type3 does not grant an adjacent tile from the previous exploration");

            before = distanceCase.Adventure.Colors.Single(color => color.Color == 1).Resource;
            distanceCase.Call(nameof(Theatre4ExploreGridRequest), new Theatre4ExploreGridRequest
            {
                MapId = chapter.MapId, PosX = distant.PosX, PosY = distant.PosY
            });
            AssertEqual(before + bonus, distanceCase.Adventure.Colors.Single(color => color.Color == 1).Resource,
                "Type3 compares the current tile with the immediately previous tile");
            AssertEqual(distant.PosY, distanceCase.Adventure.PreExplorePos!.PosY,
                "Type3 advances the previous position after the trigger");
        }
    }

    private static void ValidateTheatre4EffectAuditDailyColors()
    {
        Theatre4EffectTable flush = T4EffectAuditEffectByType(1);
        Theatre4EffectTable flowers = T4EffectAuditEffectByType(19);
        Theatre4EffectTable diversify = TableReaderV2.Parse<Theatre4EffectTable>()
            .First(row => row.Type == 38 && row.Params.Count >= 3
                && (row.Params[1] != 0 || row.Params[2] != 0));

        using Theatre4Case test = new("effect-audit-daily-colors");
        test.StartRun(1);
        Theatre4SeedEffect(test, flush.Id);
        Theatre4SeedEffect(test, flowers.Id);
        Theatre4SeedEffect(test, diversify.Id);
        test.Adventure.Ap = 100;
        test.Adventure.Gold = 0;
        Theatre4ChapterData chapter = test.Adventure.Chapters[0];
        List<Theatre4GridData> tiles = chapter.Grids
            .Where(grid => grid.PosX != grid.PosY && grid.Type != T4GridStart).Take(6).ToList();
        if (tiles.Count != 6) throw new InvalidDataException("Daily colour fixture needs six authored positions.");
        for (int index = 0; index < tiles.Count; index++)
            T4EffectAuditPrepareExploreTile(tiles[index], index < 3 ? 1 : index - 2);
        test.SaveFixture();

        Dictionary<int, (int Level, int Resource, int Daily, int Point)> before =
            T4EffectAuditColorState(test);
        foreach (Theatre4GridData tile in tiles.Take(3))
            test.Call(nameof(Theatre4ExploreGridRequest), new Theatre4ExploreGridRequest
            {
                MapId = chapter.MapId, PosX = tile.PosX, PosY = tile.PosY
            });
        AssertEqual(true, test.Adventure.DailyExploreColors.SequenceEqual([1, 1, 1]),
            "Daily exploration retains duplicate colour entries");
        JObject firstSettle = test.Call(nameof(Theatre4DailySettleRequest));
        JObject firstResult = RequiredObject(firstSettle, "SettleResult", "duplicate colour daily settle");
        JObject firstResources = RequiredObject(firstResult, "ColorResource", "duplicate colour resources");
        int flushAmount = flush.Params[1];
        foreach ((int color, (int Level, int Resource, int Daily, int Point) state) in before)
        {
            int expected = state.Resource + state.Daily + (color == 1 ? flushAmount : 0);
            AssertEqual(expected, RequiredValue<int>(firstResources, color.ToString(), JTokenType.Integer,
                "duplicate colour resources"), $"Duplicate colour settle resource {color}");
            AssertEqual(expected, test.Adventure.Colors.Single(item => item.Color == color).Resource,
                $"Duplicate colour permanent resource {color}");
        }
        AssertEqual(0, test.Adventure.CustomEffects.Single(effect => effect.EffectId == flush.Id)
            .CustomData.Values.Sum(), "Type1 daily amount is consumed after tally");
        AssertEqual(0, test.Adventure.DailyExploreColors.Count, "Daily colour list clears after tally");
        AssertEqual(0, test.Adventure.DailyExplorePosSet.Count, "Daily position list clears after tally");

        AssertEqual(1d, T4EffectAuditRequiredNumericValue(firstResult, "ColorExtra",
            "duplicate colour daily settle"),
            "One-colour day leaves Type19 multiplier neutral");
        Dictionary<int, (int Level, int Resource, int Daily, int Point)> afterFirst = T4EffectAuditColorState(test);
        test.Relog("effect-audit-daily-colors-first");
        AssertEqual(0, test.Adventure.DailyExploreColors.Count, "Relog does not restore stale daily colours");
        AssertEqual(0, test.Adventure.DailyExplorePosSet.Count, "Relog does not restore stale daily positions");
        AssertEqual(true, afterFirst.SequenceEqual(T4EffectAuditColorState(test)),
            "Relog preserves the first daily colour tally");

        test.Adventure.Ap = 100;
        test.Adventure.Gold = 0;
        chapter = test.Adventure.Chapters[0];
        for (int index = 3; index < 6; index++)
            T4EffectAuditPrepareExploreTile(test.Grid(chapter.MapId, tiles[index].PosX, tiles[index].PosY), index - 2);
        test.SaveFixture();
        before = T4EffectAuditColorState(test);
        foreach (Theatre4GridData tile in tiles.Skip(3))
            test.Call(nameof(Theatre4ExploreGridRequest), new Theatre4ExploreGridRequest
            {
                MapId = chapter.MapId, PosX = tile.PosX, PosY = tile.PosY
            });
        AssertEqual(true, test.Adventure.DailyExploreColors.SequenceEqual([1, 2, 3]),
            "Daily exploration retains all three distinct colour entries");
        JObject secondSettle = test.Call(nameof(Theatre4DailySettleRequest));
        JObject secondResult = RequiredObject(secondSettle, "SettleResult", "three-colour daily settle");
        int resourcePerColor = diversify.Params[1];
        int levelPerColor = diversify.Params[2];
        foreach ((int color, (int Level, int Resource, int Daily, int Point) state) in before)
        {
            AssertEqual(state.Resource + state.Daily + resourcePerColor,
                RequiredValue<int>(RequiredObject(secondResult, "ColorResource", "three-colour resources"),
                    color.ToString(), JTokenType.Integer, "three-colour resources"),
                $"Type38 daily resource {color}");
            AssertEqual(state.Level + levelPerColor,
                RequiredValue<int>(RequiredObject(secondResult, "ColorLevel", "three-colour levels"),
                    color.ToString(), JTokenType.Integer, "three-colour levels"),
                $"Type38 daily level {color}");
        }
        AssertEqual((10000d + flowers.Params[1]) / 10000d,
            T4EffectAuditRequiredNumericValue(secondResult, "ColorExtra", "three-colour daily settle"),
            "Type19 applies the authored multiplier only after three distinct colours");
        AssertEqual(flowers.Params[1], test.Adventure.CustomEffects.Single(effect => effect.EffectId == flowers.Id).MarkupRate,
            "Type19 stores the current day's multiplier");
        AssertEqual(0, test.Adventure.CustomEffects.Single(effect => effect.EffectId == flush.Id)
            .CustomData.Values.Sum(), "Type1 does not count one instance of each colour as three duplicates");
        AssertEqual(0, test.Adventure.DailyExploreColors.Count, "Three-colour tally clears its colour list");
        AssertEqual(0, test.Adventure.DailyExplorePosSet.Count, "Three-colour tally clears its position list");

        Dictionary<int, (int Level, int Resource, int Daily, int Point)> afterSecond = T4EffectAuditColorState(test);
        Dictionary<int, (int Level, int Resource)> afterSecondType38 = T4EffectAuditType38State(test);
        test.Relog("effect-audit-daily-colors-second");
        AssertEqual(true, afterSecond.SequenceEqual(T4EffectAuditColorState(test)),
            "Relog preserves Type19/38 daily results");
        test.Adventure.Ap = 100;
        test.Adventure.Gold = 0;
        test.SaveFixture();
        JObject thirdSettle = test.Call(nameof(Theatre4DailySettleRequest));
        JObject thirdResult = RequiredObject(thirdSettle, "SettleResult", "empty daily settle after colour tally");
        AssertEqual(1d, T4EffectAuditRequiredNumericValue(thirdResult, "ColorExtra",
            "empty daily settle after colour tally"),
            "Type19 resets at the day boundary");
        AssertEqual(0, test.Adventure.CustomEffects.Single(effect => effect.EffectId == flowers.Id).MarkupRate,
            "Type19 does not carry the previous day's multiplier");
        AssertEqual(true, afterSecondType38.SequenceEqual(T4EffectAuditType38State(test)),
            "Type38 does not restack without a new three-colour day");
        Dictionary<int, (int Level, int Resource, int Daily, int Point)> afterThird =
            T4EffectAuditColorState(test);
        AssertEqual(0, test.Adventure.DailyExploreColors.Count, "Empty day keeps the colour list clear");
        test.Relog("effect-audit-daily-colors-third");
        AssertEqual(true, afterThird.SequenceEqual(T4EffectAuditColorState(test)),
            "Colour state remains stable after the reset-day relog");
    }

    private static void ValidateTheatre4EffectAuditPersistentCounters()
    {
        Theatre4EffectTable everyThirdBlue = TableReaderV2.Parse<Theatre4EffectTable>().Single(row => row.Id == 11002);
        Theatre4EffectTable redCombat = TableReaderV2.Parse<Theatre4EffectTable>().Single(row => row.Id == 11000);
        using Theatre4Case test = new("effect-audit-counters");
        test.StartRun(1);
        Theatre4SeedEffect(test, everyThirdBlue.Id);
        Theatre4SeedEffect(test, redCombat.Id);
        test.Adventure.Ap = 100;
        test.Adventure.Bp = 0;
        Theatre4ChapterData chapter = test.Adventure.Chapters[0];
        List<Theatre4GridData> tiles = chapter.Grids
            .Where(grid => grid.PosX != grid.PosY && grid.Type != T4GridStart).Take(4).ToList();
        if (tiles.Count != 4) throw new InvalidDataException("Counter fixture needs four authored positions.");
        T4EffectAuditPrepareExploreTile(tiles[0], 3);
        T4EffectAuditPrepareExploreTile(tiles[1], 3);
        T4EffectAuditPrepareExploreTile(tiles[2], 1);
        T4EffectAuditPrepareExploreTile(tiles[3], 3);
        test.SaveFixture();

        test.Call(nameof(Theatre4ExploreGridRequest), new Theatre4ExploreGridRequest
        {
            MapId = chapter.MapId, PosX = tiles[0].PosX, PosY = tiles[0].PosY
        });
        test.Call(nameof(Theatre4ExploreGridRequest), new Theatre4ExploreGridRequest
        {
            MapId = chapter.MapId, PosX = tiles[1].PosX, PosY = tiles[1].PosY
        });
        test.Call(nameof(Theatre4ExploreGridRequest), new Theatre4ExploreGridRequest
        {
            MapId = chapter.MapId, PosX = tiles[2].PosX, PosY = tiles[2].PosY
        });
        Theatre4EffectData thirdBlue = test.Adventure.CustomEffects.Single(effect => effect.EffectId == everyThirdBlue.Id);
        Theatre4EffectData firstRed = test.Adventure.CustomEffects.Single(effect => effect.EffectId == redCombat.Id);
        AssertEqual(2, thirdBlue.Accumulate, "Type32 retains its pre-threshold count before a day boundary");
        AssertEqual(1, firstRed.CustomData.Count, "Type33 records the authored combat effect once");
        AssertEqual(redCombat.Params[3], firstRed.CustomData.Values.Single(),
            "Type33 stores the authored combat effect id");
        int bpBeforeDay = test.Adventure.Bp;
        test.Call(nameof(Theatre4DailySettleRequest));
        AssertEqual(2, test.Adventure.CustomEffects.Single(effect => effect.EffectId == everyThirdBlue.Id).Accumulate,
            "Type32 counter persists across a daily settlement");
        AssertEqual(redCombat.Params[3], test.Adventure.CustomEffects.Single(effect => effect.EffectId == redCombat.Id)
            .CustomData.Values.Single(), "Type33 event id persists across a daily settlement");
        AssertEqual(bpBeforeDay, test.Adventure.Bp, "Type32 does not pay before its authored interval");
        test.Relog("effect-audit-counters-first");
        AssertEqual(2, test.Adventure.CustomEffects.Single(effect => effect.EffectId == everyThirdBlue.Id).Accumulate,
            "Type32 counter persists across relog");
        AssertEqual(1, test.Adventure.CustomEffects.Single(effect => effect.EffectId == redCombat.Id).CustomData.Count,
            "Type33 event list persists across relog");

        test.Adventure.Ap = 100;
        test.SaveFixture();
        test.Call(nameof(Theatre4ExploreGridRequest), new Theatre4ExploreGridRequest
        {
            MapId = chapter.MapId, PosX = tiles[3].PosX, PosY = tiles[3].PosY
        });
        AssertEqual(3, test.Adventure.CustomEffects.Single(effect => effect.EffectId == everyThirdBlue.Id).Accumulate,
            "Type32 reaches its every-third threshold across days");
        AssertEqual(bpBeforeDay + everyThirdBlue.Params[5], test.Adventure.Bp,
            "Type32 grants its authored asset exactly at the threshold");
        T4EffectAuditPrepareExploreTile(test.Grid(chapter.MapId, tiles[2].PosX, tiles[2].PosY), 1);
        test.Adventure.Ap = 100;
        test.SaveFixture();
        // A distinct second red tile is required; reuse the old coordinate only after the real
        // map state is explicitly reset, so the request still travels through ExploreGrid.
        test.Call(nameof(Theatre4ExploreGridRequest), new Theatre4ExploreGridRequest
        {
            MapId = chapter.MapId, PosX = tiles[2].PosX, PosY = tiles[2].PosY
        });
        AssertEqual(2, test.Adventure.CustomEffects.Single(effect => effect.EffectId == redCombat.Id).CustomData.Count,
            "Type33 retains both authored event ids across days");
        AssertEqual(2, test.Adventure.CustomEffects.Single(effect => effect.EffectId == redCombat.Id)
            .CustomData.Values.Count(value => value == redCombat.Params[3]),
            "Type33 does not overwrite its prior event id");
        test.Call(nameof(Theatre4DailySettleRequest));
        AssertEqual(3, test.Adventure.CustomEffects.Single(effect => effect.EffectId == everyThirdBlue.Id).Accumulate,
            "Type32 counter survives the next day after paying");
        AssertEqual(2, test.Adventure.CustomEffects.Single(effect => effect.EffectId == redCombat.Id).CustomData.Count,
            "Type33 event ids survive the next day after recording");
        test.Relog("effect-audit-counters-second");
        AssertEqual(3, test.Adventure.CustomEffects.Single(effect => effect.EffectId == everyThirdBlue.Id).Accumulate,
            "Type32 threshold state is durable after the second relog");
    }

    private static void ValidateTheatre4EffectAuditShopPurchase()
    {
        Theatre4EffectTable hoarder = T4EffectAuditEffectByType(2);
        using Theatre4Case test = new("effect-audit-shop-purchase");
        test.StartRun(1);
        Theatre4SeedItem(test, 2);
        test.Adventure.Ap = 100;
        Theatre4ChapterData chapter = test.Adventure.Chapters[0];
        List<Theatre4ShopGoodsTable> allGoods = TableReaderV2.Parse<Theatre4ShopGoodsTable>();
        Theatre4ShopGroupTable group = TableReaderV2.Parse<Theatre4ShopGroupTable>().First(candidate =>
        {
            Theatre4ShopTable shop = TableReaderV2.Parse<Theatre4ShopTable>().Single(row => row.Id == candidate.ShopId);
                return shop.GoodsGroupId.Any(goodsGroup => allGoods.Any(goods => goods.GroupId == goodsGroup
                    && goods.GoodsType == T4AssetItem && goods.GoodsId is > 0 && goods.Id != goods.GoodsId
                    && !goods.ConditionId.Where(id => id > 0).Any()));
        });
        Theatre4ShopTable authoredShop = TableReaderV2.Parse<Theatre4ShopTable>().Single(row => row.Id == group.ShopId);
        List<int> authoredGoods = allGoods.Where(goods => authoredShop.GoodsGroupId.Contains(goods.GroupId)
                && goods.GoodsType == T4AssetItem && goods.GoodsId is > 0 && goods.Id != goods.GoodsId
                && !goods.ConditionId.Where(id => id > 0).Any()).Select(goods => goods.Id).Take(2).ToList();
        if (authoredGoods.Count != 2) throw new InvalidDataException("Type2 shop fixture needs two authored goods.");
        Theatre4GridData host = chapter.Grids.FirstOrDefault(grid => grid.State == T4StateUnknown
            && grid.PosX != grid.PosY && grid.Type != T4GridStart)
            ?? throw new InvalidDataException("Type2 shop fixture has no authored host tile.");
        host.Type = T4GridShop;
        host.ContentGroup = group.ShopGroupId;
        host.ContentId = authoredShop.Id;
        host.Fight = null;
        host.Event = null;
        host.Building = null;
        host.Shop = new Theatre4ShopData { ShopId = authoredShop.Id };
        host.State = T4StateUnknown;
        test.SaveFixture();
        test.ExploreUntilDiscover(chapter.MapId, host);
        test.ExploreStep(chapter.MapId, host.PosX, host.PosY);
        AssertEqual(true, test.Shop(chapter.MapId, host.PosX, host.PosY).Goods.Count >= 2,
            "Type2 purchase fixture exposes two authored shelf goods");
        int ItemGoodsIndex() => test.Shop(chapter.MapId, host.PosX, host.PosY).Goods
            .Select((goods, index) => (goods, index))
            .First(item => allGoods.Single(row => row.Id == item.goods.GoodsId).GoodsType == T4AssetItem
                && allGoods.Single(row => row.Id == item.goods.GoodsId).GoodsId != 2).index + 1;
        int firstGoodsIndex = ItemGoodsIndex();
        test.SetGold(1_000_000);

        Theatre4ItemData hoarderItem = test.Adventure.Items.Single(item => item.ItemId == 2);
        Theatre4EffectData effect = T4EffectAuditEffects(test).Single(item => item.EffectId == hoarder.Id);
        AssertEqual(hoarderItem.Uid, effect.ItemUid, "Type2 effect is attached to the owned Hoarder item");
        AssertEqual(0, effect.Count, "Type2 starts with no count state");
        AssertEqual(0, effect.ColorResource.Count, "Type2 starts with no colour-resource side state");
        AssertEqual(0, effect.MarkupRate, "Type2 starts with no markup state");
        AssertEqual(0, effect.Accumulate, "Type2 starts with no accumulation state");
        AssertEqual(0, effect.UseTimes, "Type2 starts with no skill-use state");
        Dictionary<int, int> dataBefore = new(effect.CustomData);
        Dictionary<int, int> resourcesBefore = test.Adventure.Colors.ToDictionary(color => color.Color,
            color => color.Resource);
        test.Call(nameof(Theatre4ShopBuyRequest), new Theatre4ShopBuyRequest
        {
            MapId = chapter.MapId, PosX = host.PosX, PosY = host.PosY, GoodsIndex = firstGoodsIndex
        });
        effect = T4EffectAuditEffects(test).Single(item => item.EffectId == hoarder.Id);
        int amount = hoarder.Params[0];
        AssertEqual(amount, effect.CustomData.Values.Sum() - dataBefore.Values.Sum(),
            "Type2 adds the authored amount once for one purchase");
        AssertEqual(1, effect.CustomData.Count, "Type2 records one deterministic colour bucket");
        AssertEqual(1, test.Adventure.EffectShopBuyTimes, "Type2 purchase increments its persisted UI counter once");
        int selectedColor = effect.CustomData.Single(pair => pair.Value != dataBefore.GetValueOrDefault(pair.Key)).Key;
        foreach (int color in Enumerable.Range(1, 3))
            AssertEqual(resourcesBefore[color] + effect.CustomData.GetValueOrDefault(color)
                - dataBefore.GetValueOrDefault(color),
                test.Adventure.Colors.Single(item => item.Color == color).Resource,
                $"Type2 purchase credits its selected colour {color}");
        JObject[] firstColorPushes = test.Pushes
            .Where(push => push.Name == "NotifyTheatre4ColorResourceData")
            .Select(push => JObject.Parse(MessagePackSerializer.ConvertToJson(push.Content))).ToArray();
        AssertEqual(1, firstColorPushes.Length, "Type2 purchase pushes exactly one colour-resource update");
        JObject firstColorPush = firstColorPushes.Single();
        Theatre4ColorTalentData firstColor = test.Adventure.Colors.Single(item => item.Color == selectedColor);
        AssertEqual(selectedColor, RequiredValue<int>(firstColorPush, "Color", JTokenType.Integer,
            "Type2 colour push"), "Type2 push identifies the selected random colour");
        AssertEqual(firstColor.Resource, RequiredValue<int>(firstColorPush, "Resource", JTokenType.Integer,
            "Type2 colour push"), "Type2 push carries current selected resource");
        AssertEqual(firstColor.Level, RequiredValue<int>(firstColorPush, "Level", JTokenType.Integer,
            "Type2 colour push"), "Type2 push carries current selected level");
        AssertEqual(firstColor.DailyResource, RequiredValue<int>(firstColorPush, "DailyResource", JTokenType.Integer,
            "Type2 colour push"), "Type2 push carries current daily resource");
        AssertEqual(firstColor.Point, RequiredValue<int>(firstColorPush, "Point", JTokenType.Integer,
            "Type2 colour push"), "Type2 push carries current selected point");
        AssertEqual(firstColor.PointCanCost, RequiredValue<int>(firstColorPush, "PointCanCost", JTokenType.Integer,
            "Type2 colour push"), "Type2 push carries current point budget");
        JObject firstPush = T4EffectAuditPushJson(test, "NotifyTheatre4EffectsChange");
        JObject firstPushedEffect = RequiredValue<JArray>(firstPush, "Effects", JTokenType.Array,
            "Type2 effect push").OfType<JObject>().Single(item => item.Value<int>("EffectId") == hoarder.Id);
        T4EffectAuditAssertEffectPush(firstPushedEffect, effect, "Type2 effect push");

        Dictionary<int, int> duplicateData = new(effect.CustomData);
        Dictionary<int, int> duplicateResources = test.Adventure.Colors.ToDictionary(color => color.Color,
            color => color.Resource);
        test.RepeatRejected(nameof(Theatre4ShopBuyRequest), new Theatre4ShopBuyRequest
        {
            MapId = chapter.MapId, PosX = host.PosX, PosY = host.PosY, GoodsIndex = firstGoodsIndex
        }, "Type2 duplicate purchase");
        effect = T4EffectAuditEffects(test).Single(item => item.EffectId == hoarder.Id);
        AssertEqual(true, duplicateData.SequenceEqual(effect.CustomData),
            "Type2 duplicate purchase leaves CustomData unchanged");
        AssertEqual(true, duplicateResources.SequenceEqual(test.Adventure.Colors.ToDictionary(color => color.Color,
            color => color.Resource)), "Type2 duplicate purchase leaves colour resources unchanged");
        AssertEqual(1, test.Adventure.EffectShopBuyTimes, "Type2 duplicate purchase leaves the UI counter unchanged");

        Dictionary<int, int> relogData = new(effect.CustomData);
        Dictionary<int, int> relogResources = test.Adventure.Colors.ToDictionary(color => color.Color,
            color => color.Resource);
        test.Relog("effect-audit-shop-purchase-first");
        effect = T4EffectAuditEffects(test).Single(item => item.EffectId == hoarder.Id);
        AssertEqual(true, relogData.SequenceEqual(effect.CustomData), "Type2 CustomData survives relog");
        AssertEqual(true, relogResources.SequenceEqual(test.Adventure.Colors.ToDictionary(color => color.Color,
            color => color.Resource)), "Type2 selected colour survives relog");
        test.Call(nameof(Theatre4RefreshGoodsRequest), new Theatre4RefreshGoodsRequest
        {
            MapId = chapter.MapId, PosX = host.PosX, PosY = host.PosY
        });
        effect = T4EffectAuditEffects(test).Single(item => item.EffectId == hoarder.Id);
        int secondGoodsIndex = ItemGoodsIndex();
        Dictionary<int, int> secondDataBefore = new(effect.CustomData);
        Dictionary<int, int> secondResourcesBefore = test.Adventure.Colors.ToDictionary(color => color.Color,
            color => color.Resource);
        test.Call(nameof(Theatre4ShopBuyRequest), new Theatre4ShopBuyRequest
        {
            MapId = chapter.MapId, PosX = host.PosX, PosY = host.PosY, GoodsIndex = secondGoodsIndex
        });
        effect = T4EffectAuditEffects(test).Single(item => item.EffectId == hoarder.Id);
        AssertEqual(amount * 2, effect.CustomData.Values.Sum(),
            "Type2 accumulates both purchases in persisted CustomData");
        AssertEqual(amount, effect.CustomData.Values.Sum() - secondDataBefore.Values.Sum(),
            "Type2 adds one authored amount for the second purchase");
        foreach (int color in Enumerable.Range(1, 3))
            AssertEqual(secondResourcesBefore[color] + effect.CustomData.GetValueOrDefault(color)
                - secondDataBefore.GetValueOrDefault(color),
                test.Adventure.Colors.Single(item => item.Color == color).Resource,
                $"Type2 second purchase credits its selected colour {color} once");
        AssertEqual(2, test.Adventure.EffectShopBuyTimes, "Type2's UI purchase amount persists at two");
        JObject[] secondColorPushes = test.Pushes
            .Where(push => push.Name == "NotifyTheatre4ColorResourceData")
            .Select(push => JObject.Parse(MessagePackSerializer.ConvertToJson(push.Content))).ToArray();
        AssertEqual(1, secondColorPushes.Length, "Type2 second purchase pushes one colour-resource update");
        JObject secondColorPush = secondColorPushes.Single();
        int secondColorId = RequiredValue<int>(secondColorPush, "Color", JTokenType.Integer,
            "Type2 second colour push");
        Theatre4ColorTalentData secondColor = test.Adventure.Colors.Single(item => item.Color == secondColorId);
        AssertEqual(secondColor.Resource, RequiredValue<int>(secondColorPush, "Resource", JTokenType.Integer,
            "Type2 second colour push"), "Type2 second push carries current resource");
        AssertEqual(secondColor.Level, RequiredValue<int>(secondColorPush, "Level", JTokenType.Integer,
            "Type2 second colour push"), "Type2 second push carries current level");
        AssertEqual(secondColor.DailyResource, RequiredValue<int>(secondColorPush, "DailyResource", JTokenType.Integer,
            "Type2 second colour push"), "Type2 second push carries current daily resource");
        AssertEqual(secondColor.Point, RequiredValue<int>(secondColorPush, "Point", JTokenType.Integer,
            "Type2 second colour push"), "Type2 second push carries current point");
        AssertEqual(secondColor.PointCanCost, RequiredValue<int>(secondColorPush, "PointCanCost", JTokenType.Integer,
            "Type2 second colour push"), "Type2 second push carries current point budget");
        JObject secondPush = T4EffectAuditPushJson(test, "NotifyTheatre4EffectsChange");
        JObject secondPushedEffect = RequiredValue<JArray>(secondPush, "Effects", JTokenType.Array,
            "Type2 second effect push").OfType<JObject>().Single(item => item.Value<int>("EffectId") == hoarder.Id);
        T4EffectAuditAssertEffectPush(secondPushedEffect, effect, "Type2 second effect push");
        test.Relog("effect-audit-shop-purchase-second");
        AssertEqual(amount * 2, T4EffectAuditEffects(test).Single(item => item.EffectId == hoarder.Id)
            .CustomData.Values.Sum(), "Type2 total remains durable after the second relog");

    }
    private static void ValidateTheatre4EffectAuditRecruitment()
    {
        Theatre4EffectTable mentoring = T4EffectAuditEffectByType(7);
        Theatre4EffectTable community = T4EffectAuditEffectByType(9);
        T4EffectAuditRecruitTicket(9, 1, "Red", mentoring, community, duplicate: true);
        T4EffectAuditRecruitTicket(10, 3, "Blue", mentoring, community, duplicate: false);
    }

    private static void T4EffectAuditRecruitTicket(int ticketId, int promisedColor, string promisedName,
        Theatre4EffectTable mentoring, Theatre4EffectTable community, bool duplicate)
    {
        Theatre4RecruitTicketTable ticket = TableReaderV2.Parse<Theatre4RecruitTicketTable>().Single(row => row.Id == ticketId);
        AssertEqual(true, ticket.Desc.Contains(promisedName, StringComparison.OrdinalIgnoreCase),
            $"Recruit ticket {ticketId} retains its authored {promisedName} promise");
        using Theatre4Case test = new($"effect-audit-recruit-{ticketId}");
        test.StartRun(1);
        Theatre4SeedEffect(test, mentoring.Id);
        Theatre4SeedEffect(test, community.Id);
        test.Adventure.Transactions.RemoveAll(transaction => transaction.Type == 1);
        test.Adventure.RecruitTickets.Clear();
        test.Adventure.Characters.Clear();
        test.SaveFixture();
        Theatre4AddAsset(test, T4EffectAuditAssetRecruit, ticket.Id, 1);
        Theatre4TransactionData offer = test.Adventure.Transactions.Single(transaction => transaction.Type == 1);
        // Ticket colour is authored by its description/group; CharacterStar.ColorLevel is only the UI preview.
        List<Theatre4CharacterGroupTable> authoredMembers = TableReaderV2.Parse<Theatre4CharacterGroupTable>()
            .Where(row => row.GroupId == ticket.CharacterGroupA).ToList();
        AssertEqual(true, authoredMembers.Count > 0, $"Recruit ticket {ticketId} has an authored character group");
        foreach (Theatre4CharacterGroupTable member in authoredMembers)
            AssertEqual(1, member.Star ?? 0, $"Recruit ticket {ticketId} uses authored one-star offers");
        Theatre4CharacterData offered = offer.Characters.First();
        AssertEqual(true, authoredMembers.Any(member => member.CharacterId == offered.CharacterId
                && (member.Star ?? 0) == offered.Star),
            $"Recruit ticket {ticketId} selects its promised member from the authored group");
        Dictionary<int, int> beforeStar = T4EffectAuditCharacterStarLevels(offered.CharacterId, 0);
        Dictionary<int, int> afterStar = T4EffectAuditCharacterStarLevels(offered.CharacterId, offered.Star);
        Dictionary<int, int> beforeColors = test.Adventure.Colors.ToDictionary(color => color.Color,
            color => color.Level);
        int communityBonus = community.Params.FirstOrDefault();
        Dictionary<int, int> firstDelta = T4EffectAuditRecruitDelta(beforeStar, afterStar, communityBonus);
        test.Call(nameof(Theatre4ConfirmRecruitRequest), new Theatre4ConfirmRecruitRequest
        {
            TransactionId = offer.Id, Index = 1
        });
        Theatre4CharacterData owned = test.Adventure.Characters.Single(character => character.CharacterId == offered.CharacterId);
        AssertEqual(true, firstDelta.OrderBy(pair => pair.Key).SequenceEqual(owned.ColorLevelAdds.OrderBy(pair => pair.Key)),
            $"Recruit ticket {ticketId} stores direct star delta plus Type9 once");
        foreach (int color in Enumerable.Range(1, 3))
            AssertEqual(beforeColors[color] + firstDelta.GetValueOrDefault(color),
                test.Adventure.Colors.Single(item => item.Color == color).Level,
                $"Recruit ticket {ticketId} applies the contribution to colour {color}");
        JObject update = T4EffectAuditPushJson(test, "NotifyTheatre4CharacterUpdate");
        JObject pushedCharacter = RequiredObject(update, "Character", $"Recruit ticket {ticketId} character push");
        AssertEqual(true, JToken.DeepEquals(JObject.FromObject(owned.ColorLevelAdds),
            RequiredObject(pushedCharacter, "ColorLevelAdds", $"Recruit ticket {ticketId} character push")),
            $"Recruit ticket {ticketId} pushes the contribution visible to the UI");
        test.Relog($"effect-audit-recruit-{ticketId}-first");
        AssertEqual(true, firstDelta.OrderBy(pair => pair.Key).SequenceEqual(test.Adventure.Characters
            .Single(character => character.CharacterId == offered.CharacterId).ColorLevelAdds.OrderBy(pair => pair.Key)),
            $"Recruit ticket {ticketId} contribution survives relog");

        if (!duplicate)
        {
            int levelBeforeDaily = test.Adventure.Colors.Single(item => item.Color == promisedColor).Level;
            test.Adventure.Ap = 100;
            test.Adventure.Gold = 0;
            test.SaveFixture();
            test.Call(nameof(Theatre4DailySettleRequest));
            AssertEqual(levelBeforeDaily, test.Adventure.Colors.Single(item => item.Color == promisedColor).Level,
                $"Recruit ticket {ticketId} contribution is not restacked by daily settlement");
            test.Relog($"effect-audit-recruit-{ticketId}-daily");
            return;
        }

        Theatre4CharacterData existing = test.Adventure.Characters.Single(character => character.CharacterId == offered.CharacterId);
        int nextStar = existing.Star + 1;
        if (!T4EffectAuditCharacterStarLevels(existing.CharacterId, nextStar).Any())
            throw new InvalidDataException($"Recruit ticket {ticketId} has no authored duplicate star.");
        Theatre4TransactionData duplicateOffer = new()
        {
            Id = ++test.Adventure.IdSequence,
            Type = 1,
            Characters = [new Theatre4CharacterData { CharacterId = existing.CharacterId, Star = nextStar }],
            SelectLimit = 1
        };
        test.Adventure.Transactions.Add(duplicateOffer);
        test.SaveFixture();
        Dictionary<int, int> duplicateBeforeStar = T4EffectAuditCharacterStarLevels(existing.CharacterId, existing.Star);
        Dictionary<int, int> duplicateAfterStar = T4EffectAuditCharacterStarLevels(existing.CharacterId, nextStar);
        Dictionary<int, int> duplicateDelta = T4EffectAuditRecruitDelta(duplicateBeforeStar, duplicateAfterStar,
            mentoring.Params.FirstOrDefault());
        Dictionary<int, int> colorsBeforeDuplicate = test.Adventure.Colors.ToDictionary(color => color.Color,
            color => color.Level);
        test.Call(nameof(Theatre4ConfirmRecruitRequest), new Theatre4ConfirmRecruitRequest
        {
            TransactionId = duplicateOffer.Id, Index = 1
        });
        Theatre4CharacterData upgraded = test.Adventure.Characters.Single(character => character.CharacterId == existing.CharacterId);
        Dictionary<int, int> expectedAdds = firstDelta.ToDictionary(pair => pair.Key,
            pair => pair.Value + duplicateDelta.GetValueOrDefault(pair.Key));
        AssertEqual(nextStar, upgraded.Star, "Duplicate recruitment advances to the next authored star");
        AssertEqual(true, expectedAdds.OrderBy(pair => pair.Key).SequenceEqual(upgraded.ColorLevelAdds.OrderBy(pair => pair.Key)),
            "Duplicate recruitment adds the direct star delta plus Type7 once");
        foreach (int color in Enumerable.Range(1, 3))
            AssertEqual(colorsBeforeDuplicate[color] + duplicateDelta.GetValueOrDefault(color),
                test.Adventure.Colors.Single(item => item.Color == color).Level,
                $"Duplicate recruitment applies Type7 to colour {color} once");
        test.Adventure.Ap = 100;
        test.Adventure.Gold = 0;
        test.SaveFixture();
        test.Call(nameof(Theatre4DailySettleRequest));
        AssertEqual(true, expectedAdds.OrderBy(pair => pair.Key).SequenceEqual(test.Adventure.Characters
            .Single(character => character.CharacterId == existing.CharacterId).ColorLevelAdds.OrderBy(pair => pair.Key)),
            "Recruitment contribution is not restacked by daily settlement");
        test.Relog("effect-audit-recruit-duplicate");
        AssertEqual(true, expectedAdds.OrderBy(pair => pair.Key).SequenceEqual(test.Adventure.Characters
            .Single(character => character.CharacterId == existing.CharacterId).ColorLevelAdds.OrderBy(pair => pair.Key)),
            "Duplicate recruitment contribution survives relog");
    }

    private static void ValidateTheatre4EffectAuditMapShapes()
    {
        Theatre4EffectTable straight = T4EffectAuditEffectByType(12);
        Theatre4EffectTable corner = T4EffectAuditEffectByType(29);
        Theatre4EffectTable timeback = T4EffectAuditEffectByType(423);
        using (Theatre4Case test = new("effect-audit-shape"))
        {
            test.StartRun(1);
            Theatre4SeedEffect(test, straight.Id);
            Theatre4SeedEffect(test, corner.Id);
            Theatre4SeedEffect(test, timeback.Id);
            test.Adventure.Gold = 0;
            Theatre4ChapterData chapter = test.Adventure.Chapters[0];
            List<Theatre4GridData> shape = T4EffectAuditSameMapLineAndCorner(chapter);
            test.Adventure.Ap = shape.Count;
            foreach (Theatre4GridData grid in shape) T4EffectAuditPrepareExploreTile(grid, 1);
            test.SaveFixture();
            List<(int MapId, int X, int Y)> expectedPositions = shape
                .Select(grid => (chapter.MapId, grid.PosX, grid.PosY)).ToList();
            Dictionary<int, (int Level, int Resource, int Daily, int Point)> before = T4EffectAuditColorState(test);
            foreach (Theatre4GridData grid in shape)
                test.Call(nameof(Theatre4ExploreGridRequest), new Theatre4ExploreGridRequest
                {
                    MapId = chapter.MapId, PosX = grid.PosX, PosY = grid.PosY
                });
            AssertEqual(true, expectedPositions.SequenceEqual(test.Adventure.DailyExplorePosSet
                .Select(pos => (pos.MapId, pos.PosX, pos.PosY))),
                "Same-map explored positions retain every entered coordinate in order");
            JObject settle = test.Call(nameof(Theatre4DailySettleRequest));
            JObject result = RequiredObject(settle, "SettleResult", "same-map shape settle");
            JObject resources = RequiredObject(result, "ColorResource", "same-map shape resources");
            foreach ((int color, (int Level, int Resource, int Daily, int Point) state) in before)
                AssertEqual(state.Resource + state.Daily + straight.Params[0],
                    RequiredValue<int>(resources, color.ToString(), JTokenType.Integer, "same-map shape resources"),
                    $"Straight-line tally adds authored colour resource {color}");
            AssertEqual(corner.Params[0], test.Adventure.Gold,
                "Corner tally grants its authored gold amount");
            AssertEqual(0, test.Adventure.DailyExplorePosSet.Count, "Shape positions clear after tally");
            test.Relog("effect-audit-shape-settled");
            AssertEqual(0, test.Adventure.DailyExplorePosSet.Count, "Settled shape does not return after relog");

            test.Adventure.Ap = 100;
            test.Adventure.Gold = 0;
            test.SaveFixture();
            int tracebackDays = chapter.MaxTracebackDays;
            while (test.Adventure.Days < tracebackDays)
                test.Call(nameof(Theatre4DailySettleRequest));
            Theatre4AddAsset(test, T4AssetTimeBack, 0, 1);
            test.Call(nameof(Theatre4UseSkillEffectRequest), new Theatre4UseSkillEffectRequest
            {
                EffectId = timeback.Id, Params = []
            });
            AssertEqual(0, test.Adventure.Days, "Rollback restores the pre-settlement day");
            AssertEqual(true, expectedPositions.SequenceEqual(test.Adventure.DailyExplorePosSet
                .Select(pos => (pos.MapId, pos.PosX, pos.PosY))),
                "Rollback restores the matching frozen shape, not a later stale day");
            test.Relog("effect-audit-shape-rollback");
            AssertEqual(true, expectedPositions.SequenceEqual(test.Adventure.DailyExplorePosSet
                .Select(pos => (pos.MapId, pos.PosX, pos.PosY))),
                "Relog preserves the rollback shape snapshot");
            test.Adventure.Ap = 100;
            test.Adventure.Gold = 0;
            test.SaveFixture();
            test.Call(nameof(Theatre4DailySettleRequest));
            AssertEqual(0, test.Adventure.DailyExplorePosSet.Count,
                "Replayed rollback day clears its restored shape after tally");
        }

        using Theatre4Case crossMap = new("effect-audit-shape-cross-map");
        crossMap.StartRun(1);
        Theatre4SeedEffect(crossMap, straight.Id);
        Theatre4SeedEffect(crossMap, corner.Id);
        crossMap.Adventure.Ap = 100;
        crossMap.Adventure.Gold = 0;
        Theatre4ChapterData firstChapter = crossMap.Adventure.Chapters[0];
        Theatre4ChapterData secondChapter = Theatre4InvokeInitializeMap(crossMap, 11002);
        firstChapter = crossMap.Chapter(firstChapter.MapId);
        AssertEqual(false, firstChapter.MapId == secondChapter.MapId,
            "Cross-map fixture uses two distinct authored map identities");
        List<(Theatre4ChapterData Chapter, Theatre4GridData Grid)> cross = T4EffectAuditCrossMapLine(
            firstChapter, secondChapter);
        foreach ((Theatre4ChapterData _, Theatre4GridData grid) in cross)
            T4EffectAuditPrepareExploreTile(grid, 1);
        crossMap.SaveFixture();
        Dictionary<int, (int Level, int Resource, int Daily, int Point)> crossBefore = T4EffectAuditColorState(crossMap);
        foreach ((Theatre4ChapterData chapter, Theatre4GridData grid) in cross)
            crossMap.Call(nameof(Theatre4ExploreGridRequest), new Theatre4ExploreGridRequest
            {
                MapId = chapter.MapId, PosX = grid.PosX, PosY = grid.PosY
            });
        AssertEqual(3, crossMap.Adventure.DailyExplorePosSet.Count,
            "Cross-map fixture retains all explored positions");
        JObject crossSettle = crossMap.Call(nameof(Theatre4DailySettleRequest));
        JObject crossResult = RequiredObject(crossSettle, "SettleResult", "cross-map shape settle");
        JObject crossResources = RequiredObject(crossResult, "ColorResource", "cross-map shape resources");
        foreach ((int color, (int Level, int Resource, int Daily, int Point) state) in crossBefore)
            AssertEqual(state.Resource + state.Daily,
                RequiredValue<int>(crossResources, color.ToString(), JTokenType.Integer, "cross-map shape resources"),
                $"Cross-map cells do not synthesize a same-map straight or corner for colour {color}");
        AssertEqual(0, crossMap.Adventure.Gold, "Cross-map cells do not grant the corner gold bonus");
        AssertEqual(0, crossMap.Adventure.DailyExplorePosSet.Count, "Cross-map position list clears after tally");
        crossMap.Relog("effect-audit-shape-cross-map");
        AssertEqual(0, crossMap.Adventure.DailyExplorePosSet.Count,
            "Cross-map settled positions do not return after relog");
    }

    private static Theatre4EffectTable T4EffectAuditEffectByType(int type) =>
        TableReaderV2.Parse<Theatre4EffectTable>().First(row => row.Type == type);

    private static double T4EffectAuditRequiredNumericValue(JObject payload, string propertyName,
        string endpoint)
    {
        if (!payload.TryGetValue(propertyName, out JToken? token))
            throw new InvalidDataException($"{endpoint}: missing JSON field '{propertyName}'.");
        if (token.Type is not (JTokenType.Integer or JTokenType.Float))
            throw new InvalidDataException(
                $"{endpoint} {propertyName}: expected JSON numeric value, got {token.Type}.");
        return token.Value<double>();
    }

    private static void T4EffectAuditPrepareExploreTile(Theatre4GridData grid, int color)
    {
        if (grid.PosX == grid.PosY || grid.Type == T4GridStart)
            throw new InvalidDataException("Effect audit fixture requires a non-diagonal non-start tile.");
        grid.Type = T4GridEmpty;
        grid.State = T4StateDiscover;
        grid.Color = color;
        grid.ColorResource = 0;
        grid.ContentGroup = 0;
        grid.ContentId = 0;
        grid.Fight = null;
        grid.Shop = null;
        grid.Event = null;
        grid.Building = null;
    }

    private static IEnumerable<Theatre4EffectData> T4EffectAuditEffects(Theatre4Case test) =>
        test.Adventure.CustomEffects.Concat(test.Adventure.Items.Concat(test.Adventure.Props)
            .SelectMany(item => item.Effects));

    private static void T4EffectAuditAssertEffectPush(JObject payload, Theatre4EffectData effect, string endpoint)
    {
        AssertEqual(effect.Id, RequiredValue<int>(payload, "Id", JTokenType.Integer, endpoint),
            $"{endpoint} keeps effect instance id");
        AssertEqual(effect.EffectId, RequiredValue<int>(payload, "EffectId", JTokenType.Integer, endpoint),
            $"{endpoint} keeps effect id");
        AssertEqual(effect.Count, RequiredValue<int>(payload, "Count", JTokenType.Integer, endpoint),
            $"{endpoint} keeps count");
        AssertEqual(true, JToken.DeepEquals(JObject.FromObject(effect.CustomData),
            RequiredObject(payload, "CustomData", endpoint)), $"{endpoint} pushes accumulated CustomData");
        AssertEqual(true, JToken.DeepEquals(JObject.FromObject(effect.ColorResource),
            RequiredObject(payload, "ColorResource", endpoint)), $"{endpoint} pushes empty ColorResource state");
        AssertEqual(effect.MarkupRate, RequiredValue<int>(payload, "MarkupRate", JTokenType.Integer, endpoint),
            $"{endpoint} keeps MarkupRate");
        AssertEqual(effect.ItemUid, RequiredValue<int>(payload, "ItemUid", JTokenType.Integer, endpoint),
            $"{endpoint} keeps Hoarder ItemUid");
        AssertEqual(effect.Accumulate, RequiredValue<int>(payload, "Accumulate", JTokenType.Integer, endpoint),
            $"{endpoint} keeps Accumulate");
        AssertEqual(effect.UseTimes, RequiredValue<int>(payload, "UseTimes", JTokenType.Integer, endpoint),
            $"{endpoint} keeps UseTimes");
    }

    private static Dictionary<int, (int Level, int Resource, int Daily, int Point)> T4EffectAuditColorState(
        Theatre4Case test) => test.Adventure.Colors.ToDictionary(color => color.Color,
            color => (color.Level, color.Resource, color.DailyResource, color.Point));
    // Type38 owns colour level/resource; Point is the independent daily prosperity result.
    private static Dictionary<int, (int Level, int Resource)> T4EffectAuditType38State(
        Theatre4Case test) => test.Adventure.Colors.ToDictionary(color => color.Color,
            color => (color.Level, color.Resource));

    private static Dictionary<int, int> T4EffectAuditCharacterStarLevels(int characterId, int star)
    {
        Theatre4CharacterTable character = TableReaderV2.Parse<Theatre4CharacterTable>()
            .Single(row => row.Id == characterId);
        Theatre4CharacterStarTable? row = TableReaderV2.Parse<Theatre4CharacterStarTable>()
            .FirstOrDefault(candidate => candidate.GroupId == character.StarGroupId && candidate.Star == star);
        if (row is null) return [];
        return row.ColorLevel.Select((value, index) => (Color: index + 1, Value: value))
            .Where(pair => pair.Value != 0).ToDictionary(pair => pair.Color, pair => pair.Value);
    }

    private static Dictionary<int, int> T4EffectAuditRecruitDelta(IReadOnlyDictionary<int, int> before,
        IReadOnlyDictionary<int, int> after, int bonus) => Enumerable.Range(1, 3)
        .Where(color => after.GetValueOrDefault(color) > 0)
        .ToDictionary(color => color, color => after.GetValueOrDefault(color)
            - before.GetValueOrDefault(color) + bonus);

    private static JObject T4EffectAuditPushJson(Theatre4Case test, string pushName)
    {
        Packet.Push push = test.Pushes.LastOrDefault(candidate => candidate.Name == pushName)
            ?? throw new InvalidDataException($"Missing Theatre4 push {pushName}.");
        return JObject.Parse(MessagePackSerializer.ConvertToJson(push.Content));
    }

    private static List<Theatre4GridData> T4EffectAuditSameMapLineAndCorner(Theatre4ChapterData chapter)
    {
        Theatre4MapTable map = TableReaderV2.Parse<Theatre4MapTable>()
            .SingleOrDefault(row => row.Id == chapter.MapId)
            ?? throw new InvalidDataException($"Shape fixture map {chapter.MapId} is not authored.");
        for (int y = 0; y < map.SizeY - 1; y++)
        {
            for (int x = 0; x < map.SizeX - 2; x++)
            {
                (int X, int Y)[] wanted = [(x, y), (x + 1, y), (x + 2, y), (x + 1, y + 1)];
                if (wanted.Any(pos => pos.X == pos.Y
                        || !T4EffectAuditShapePositionAvailable(chapter, map, pos.X, pos.Y)))
                    continue;
                return wanted.Select(pos => T4EffectAuditAuthoredGrid(chapter, map, pos.X, pos.Y)).ToList();
            }
        }
        throw new InvalidDataException(
            $"Shape fixture map {chapter.MapId} has no authored in-bounds line and right angle.");
    }

    private static List<(Theatre4ChapterData Chapter, Theatre4GridData Grid)> T4EffectAuditCrossMapLine(
        Theatre4ChapterData first, Theatre4ChapterData second)
    {
        Theatre4MapTable firstMap = TableReaderV2.Parse<Theatre4MapTable>()
            .SingleOrDefault(row => row.Id == first.MapId)
            ?? throw new InvalidDataException($"Cross-map shape fixture map {first.MapId} is not authored.");
        Theatre4MapTable secondMap = TableReaderV2.Parse<Theatre4MapTable>()
            .SingleOrDefault(row => row.Id == second.MapId)
            ?? throw new InvalidDataException($"Cross-map shape fixture map {second.MapId} is not authored.");
        int width = Math.Min(firstMap.SizeX - 1, secondMap.SizeX - 2);
        int height = Math.Min(firstMap.SizeY, secondMap.SizeY);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                (int X, int Y)[] wanted = [(x, y), (x + 1, y), (x + 2, y)];
                if (wanted.Any(pos => pos.X == pos.Y)
                    || wanted[..2].Any(pos =>
                        !T4EffectAuditShapePositionAvailable(first, firstMap, pos.X, pos.Y))
                    || !T4EffectAuditShapePositionAvailable(second, secondMap,
                        wanted[2].X, wanted[2].Y))
                    continue;
                Theatre4GridData firstGrid = T4EffectAuditAuthoredGrid(first, firstMap,
                    wanted[0].X, wanted[0].Y);
                Theatre4GridData secondFirst = T4EffectAuditAuthoredGrid(first, firstMap,
                    wanted[1].X, wanted[1].Y);
                Theatre4GridData third = T4EffectAuditAuthoredGrid(second, secondMap,
                    wanted[2].X, wanted[2].Y);
                return [(first, firstGrid), (first, secondFirst), (second, third)];
            }
        }
        throw new InvalidDataException(
            $"Cross-map shape fixture maps {first.MapId}/{second.MapId} have no authored coordinate split.");
    }

    private static bool T4EffectAuditShapePositionAvailable(Theatre4ChapterData chapter,
        Theatre4MapTable map, int x, int y)
    {
        Theatre4GridData? existing = chapter.Grids.SingleOrDefault(grid =>
            grid.PosX == x && grid.PosY == y);
        if (existing is not null)
            return existing.Type is not (T4GridNothing or T4GridStart);
        return !T4EffectAuditAuthoredHiddenCell(map, x, y);
    }

    private static bool T4EffectAuditAuthoredHiddenCell(Theatre4MapTable map, int x, int y)
    {
        foreach (int hiddenId in map.HiddenId.Where(id => id > 0))
        {
            foreach (Theatre4MapHiddenGridTable region in TableReaderV2.Parse<Theatre4MapHiddenGridTable>()
                .Where(row => row.Id == hiddenId))
            {
                foreach (string encoded in new[] { region.GridPos, region.Point }
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .SelectMany(value => value!.Split('|')))
                {
                    string[] coordinates = encoded.Split(',');
                    if (coordinates.Length == 2
                        && int.TryParse(coordinates[0], out int parsedX)
                        && int.TryParse(coordinates[1], out int parsedY)
                        && parsedX == x && parsedY == y)
                        return true;
                }
            }
        }
        return false;
    }

    private static Theatre4GridData T4EffectAuditAuthoredGrid(Theatre4ChapterData chapter,
        Theatre4MapTable map, int x, int y)
    {
        if (x < 0 || y < 0 || x >= map.SizeX || y >= map.SizeY)
            throw new InvalidDataException(
                $"Shape fixture coordinate {chapter.MapId}/{x}/{y} is outside the authored map.");
        if (!T4EffectAuditShapePositionAvailable(chapter, map, x, y))
            throw new InvalidDataException(
                $"Shape fixture coordinate {chapter.MapId}/{x}/{y} is not source-opened.");
        Theatre4GridData? grid = chapter.Grids.SingleOrDefault(candidate =>
            candidate.PosX == x && candidate.PosY == y);
        if (grid is not null) return grid;
        grid = new Theatre4GridData
        {
            GridId = 10000 + x * 100 + y,
            PosX = x,
            PosY = y,
            Type = T4GridEmpty,
            State = T4StateDiscover
        };
        chapter.Grids.Add(grid);
        return grid;
    }
}
