using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.client.draw;
using AscNet.Table.V2.share.draw;
using System.Globalization;

namespace AscNet.GameServer.Game;

internal static partial class DrawManager
{
    // Inheritance and guarantee rules transcribed from client DrawGroupRule. See
    // Resources/Configs/draw-rules-source.md for version-dependent decisions.
    private static readonly Dictionary<int, DrawServerRuleTable> Rules =
        TableReaderV2.Parse<DrawServerRuleTable>().ToDictionary(x => x.GroupId);

    private static readonly Dictionary<int, int> InitialCharacterQuality = CharacterQualities
        .GroupBy(x => x.CharacterId).ToDictionary(x => x.Key, x => x.Min(q => q.Quality));
    private static readonly Dictionary<int, int[]> PreviewPools = DrawPreviews.ToDictionary(x => x.Id,
        x => x.GoodsId.Concat(x.UpGoodsId).Where(id => id > 0).Distinct().ToArray());
    private static readonly Dictionary<int, double> BannerTargetProbabilities =
        TableReaderV2.Parse<DrawAimProbabilityTable>()
            .Where(x => !string.IsNullOrWhiteSpace(x.UpProbabilityPercent))
            .ToDictionary(x => x.Id, x => Probability(x.UpProbabilityPercent!));
    private static readonly Lazy<Dictionary<int, double[]>> VariablePityDistributions = new(BuildVariablePityDistributions);

    private static DrawServerRuleTable Rule(DrawInfo draw) => Rules.TryGetValue(draw.GroupId, out var rule)
        ? rule : throw new InvalidDataException($"Missing draw rules for group {draw.GroupId}");

    private static double Probability(string value) => double.Parse(value.Trim().TrimEnd('%'), CultureInfo.InvariantCulture) / 100d;

    private static double RareProbability(DrawInfo draw)
    {
        var profile = DrawProbShowsById[draw.Id];
        int baseIndex = profile.Name.FindIndex(name => name.Contains("Base Drop", StringComparison.OrdinalIgnoreCase));
        if (baseIndex >= 0) return Probability(profile.ProbShow[baseIndex]);
        if (RewardKind(draw) == 2)
            return profile.Name.Select((name, i) => name.Replace(" ", "").Contains("6★") ? Probability(profile.ProbShow[i]) : 0).Sum();
        throw new InvalidDataException($"Missing base probability for draw {draw.Id}");
    }

    private static PlayerDrawPityRound GetPityRound(Player player, DrawInfo draw, Random random)
    {
        EnsureState(player);
        var rule = Rule(draw);
        if (player.DrawState.PityRounds.TryGetValue(rule.PityGroupId, out var round)) return round;

        // The old counter was lifetime progress. Preserve its current partial cycle
        // when upgrading, then maintain a separate counter since the last rare result.
        int oldCount = GetPityCount(player, draw.GroupId);
        round = new() { HasObtainedRare = oldCount >= rule.FirstPity && oldCount > 0 };
        round.Limit = NextLimit(draw, rule, round, random);
        round.Misses = oldCount % (oldCount < rule.FirstPity ? rule.FirstPity : rule.PityMax);
        // A sampled Fate limit below the inherited progress means the next pull is due.
        player.DrawState.PityRounds[rule.PityGroupId] = round;
        return round;
    }

    public static bool InitializePityState(Player player, int groupId = 0)
    {
        EnsureState(player);
        int before = player.DrawState.PityRounds.Count;
        IEnumerable<DrawInfo> draws = groupId == 0
            ? DrawTemplates.Where(IsActive)
            : DrawsByGroup.GetValueOrDefault(groupId, []).Where(IsActive);
        foreach (DrawInfo draw in draws.Where(x => x.GroupId != 1 && Rules.ContainsKey(x.GroupId))
                     .GroupBy(x => Rule(x).PityGroupId).Select(x => x.First()))
            GetPityRound(player, draw, Random.Shared);
        return player.DrawState.PityRounds.Count != before;
    }

    private static int NextLimit(DrawInfo draw, DrawServerRuleTable rule, PlayerDrawPityRound round, Random random)
    {
        if (!round.HasObtainedRare && rule.FirstPity > 0) return rule.FirstPity;
        if (rule.PityMin == rule.PityMax) return rule.PityMin;
        double roll = random.NextDouble();
        double cumulative = 0;
        double[] weights = VariablePityDistributions.Value[rule.GroupId];
        for (int i = 0; i < weights.Length; i++)
        {
            cumulative += weights[i];
            if (roll < cumulative) return rule.PityMin + i;
        }
        return rule.PityMax;
    }

    private static Dictionary<int, double[]> BuildVariablePityDistributions()
    {
        Dictionary<int, DrawGroupRuleTable> clientRules = TableReaderV2.Parse<DrawGroupRuleTable>().ToDictionary(x => x.Id);
        Dictionary<int, double[]> result = new();
        foreach (DrawServerRuleTable rule in Rules.Values.Where(x => x.PityMin < x.PityMax && DrawTemplates.Any(d => d.GroupId == x.GroupId)))
        {
            DrawGroupRuleTable clientRule = clientRules[rule.GroupId];
            string referenceTitle = clientRule.TitleCN.StartsWith("Fate ", StringComparison.Ordinal)
                ? clientRule.TitleCN[5..] : throw new InvalidDataException($"Variable pity group {rule.GroupId} has no rate reference");
            DrawGroupRuleTable referenceClientRule = clientRules.Values.Single(x => x.TitleCN == referenceTitle);
            DrawServerRuleTable referenceRule = Rules[referenceClientRule.Id];
            DrawInfo draw = DrawTemplates.First(x => x.GroupId == rule.GroupId);
            DrawInfo referenceDraw = DrawTemplates.First(x => x.GroupId == referenceRule.GroupId);
            double baseRate = RareProbability(draw);
            double referenceBaseRate = RareProbability(referenceDraw);
            double referenceOverallRate = referenceBaseRate /
                (1 - Math.Pow(1 - referenceBaseRate, referenceRule.PityMax));
            double requiredPowerMean = 1 - baseRate / referenceOverallRate;
            double missRate = 1 - baseRate;
            double minPower = Math.Pow(missRate, rule.PityMax);
            double maxPower = Math.Pow(missRate, rule.PityMin);
            if (requiredPowerMean < minPower || requiredPowerMean > maxPower)
                throw new InvalidDataException($"Variable pity group {rule.GroupId} cannot match group {referenceRule.GroupId} overall rate");

            // The client specifies the integer range and combined rate, but not server weights.
            // Maximum entropy supplies the least-assumptive full-support distribution satisfying both constraints.
            int count = rule.PityMax - rule.PityMin + 1;
            double low = -32, high = 32;
            for (int iteration = 0; iteration < 100; iteration++)
            {
                double slope = (low + high) / 2;
                double mean = ExponentialPowerMean(rule.PityMin, count, missRate, slope);
                if (mean > requiredPowerMean) low = slope; else high = slope;
            }
            double finalSlope = (low + high) / 2;
            double[] weights = Enumerable.Range(0, count).Select(i => Math.Exp(finalSlope * (i - count + 1))).ToArray();
            double total = weights.Sum();
            for (int i = 0; i < weights.Length; i++) weights[i] /= total;
            result.Add(rule.GroupId, weights);
        }
        return result;
    }

    private static double ExponentialPowerMean(int minimum, int count, double missRate, double slope)
    {
        double total = 0, weighted = 0;
        for (int i = 0; i < count; i++)
        {
            double weight = Math.Exp(slope * (i - count + 1));
            total += weight;
            weighted += weight * Math.Pow(missRate, minimum + i);
        }
        return weighted / total;
    }

    private static double OverallRareProbability(DrawInfo draw)
    {
        DrawServerRuleTable rule = Rule(draw);
        double baseRate = RareProbability(draw);
        double[] weights = rule.PityMin == rule.PityMax ? [1] : VariablePityDistributions.Value[rule.GroupId];
        double missPowerMean = weights.Select((weight, index) => weight * Math.Pow(1 - baseRate, rule.PityMin + index)).Sum();
        return baseRate / (1 - missPowerMean);
    }

    private static int[] PreviewIds(DrawInfo draw)
    {
        return PreviewPools.TryGetValue(draw.Id, out var ids) ? ids
            : throw new InvalidDataException($"Missing draw preview {draw.Id}");
    }

    // DrawType controls client presentation; character and CUB banners both use 3.
    private static int RewardKind(DrawInfo draw)
    {
        var profile = DrawProbShowsById[draw.Id];
        if (profile.Name.Any(x => x.Contains("CUB"))) return 3;
        if (profile.Name.Any(x => x.Contains("6★"))) return 2;
        return 1;
    }
    private static int[] RarePool(DrawInfo draw)
    {
        int[] ids = PreviewIds(draw);
        return RewardKind(draw) switch
        {
            2 => ids.Where(id => Equips.Any(x => x.Id == id && x.Type > 0 && x.Quality == 6 && Character.IsOwnableEquipTemplate(x))).ToArray(),
            3 => ids.Where(id => Partners.Any(x => x.Id == id && x.InitQuality == 3)).ToArray(),
            _ => ids.Where(id => InitialCharacterQuality.GetValueOrDefault(id) == 3).ToArray()
        };
    }

    private static RewardGoods RollDraw(Player player, DrawInfo draw, Random random)
    {
        var rule = Rule(draw);
        var round = GetPityRound(player, draw, random);
        bool rare = round.Misses + 1 >= round.Limit || random.NextDouble() < RareProbability(draw);
        RewardGoods reward;
        if (rare)
        {
            int[] pool = RarePool(draw);
            int target = draw.ResourceIds.GetValueOrDefault(1);
            bool featured = DrawServerCatalog.Any(x => x.Id == draw.Id && x.TargetId == target);
            double targetProbability = BannerTargetProbabilities.GetValueOrDefault(draw.Id,
                (featured ? rule.FeaturedTargetPercent : rule.TargetPercent) / 100d);
            bool hasTarget = targetProbability > 0 && pool.Contains(target);
            if (targetProbability > 0 && !hasTarget)
                throw new InvalidDataException($"Draw {draw.Id} target {target} is missing from its highest-rarity preview");
            bool hit = hasTarget && (round.GuaranteedTarget || random.NextDouble() < targetProbability);
            int[] candidates = hasTarget && !hit ? pool.Where(id => id != target).ToArray() : pool;
            if (!hit && candidates.Length == 0)
                throw new InvalidDataException($"Draw {draw.Id} has no eligible highest-rarity outcomes");
            int id = hit ? target : candidates[random.Next(candidates.Length)];
            reward = Create(RewardKind(draw) switch { 2 => RewardType.Equip, 3 => RewardType.Partner, _ => RewardType.Character }, id, 1, 1);
            round.GuaranteedTarget = rule.Calibration != 0 && hasTarget && id != target;
            round.HasObtainedRare = true;
            round.Misses = 0;
            round.Limit = NextLimit(draw, rule, round, random);
        }
        else
        {
            reward = RollNonRare(draw, random, rule.LowerPity > 0 && round.LowerMisses + 1 >= rule.LowerPity);
            round.Misses++;
        }
        bool lowerOrBetter = rare || (RewardKind(draw) switch
        {
            2 => reward.RewardType == (int)RewardType.Equip && Equips.Any(x => x.Id == reward.TemplateId && x.Quality >= 5),
            3 => reward.RewardType == (int)RewardType.Partner,
            _ => reward.RewardType == (int)RewardType.Character && InitialCharacterQuality.GetValueOrDefault(reward.TemplateId) >= 2
        });
        round.LowerMisses = lowerOrBetter ? 0 : round.LowerMisses + 1;
        return reward;
    }

    private static RewardGoods RollNonRare(DrawInfo draw, Random random, bool forceLower = false)
    {
        // Only conditional non-rare weights are used here; S/6-star is rolled once.
        var profile = DrawProbShowsById[draw.Id];
        var entries = profile.Name.Select((name, i) => (Name: name.Replace("6 ★", "6★").Replace("5 ★", "5★"), Weight: Probability(profile.ProbShow[i])))
            .Where(x => !x.Name.Contains("S-Rank") && !x.Name.Contains("6★"))
            .Where(x => !forceLower || x.Name.Contains("5★") || x.Name.Contains("A-Rank") || x.Name.Contains("A, B-Rank")).ToArray();
        double roll = random.NextDouble() * entries.Sum(x => x.Weight);
        string category = entries.Last().Name;
        foreach (var entry in entries)
        {
            roll -= entry.Weight;
            if (roll < 0) { category = entry.Name; break; }
        }
        if (RewardKind(draw) == 2)
        {
            int quality = category.Contains("5★") ? 5 : category.Contains("4★") ? 4 : category.Contains("3★") ? 3 : 0;
            var pool = Equips.Where(x => x.Type > 0 && x.Quality == quality && Character.IsOwnableEquipTemplate(x)
                && (category.Contains('】') || category.Contains(']') || quality < 5 || Rule(draw).TargetPercent == 0 || PreviewIds(draw).Contains(x.Id)))
                .Where(x => !category.Contains('】') && !category.Contains(']') || category.EndsWith(x.Name, StringComparison.Ordinal))
                .Select(x => x.Id).ToArray();
            if (pool.Length > 0) return Create(RewardType.Equip, pool[random.Next(pool.Length)], 1, 1);
        }
        if (RewardKind(draw) == 3 && category.Contains("A-Rank"))
        {
            int[] pool = PreviewIds(draw).Where(id => Partners.Any(x => x.Id == id && x.InitQuality < 3)).ToArray();
            if (pool.Length > 0) return Create(RewardType.Partner, pool[random.Next(pool.Length)], 1, 1);
        }
        if (category.Contains("A, B-Rank"))
        {
            int[] pool = PreviewIds(draw).Where(id => InitialCharacterQuality.GetValueOrDefault(id) is 1 or 2)
                .Where(id => !forceLower || InitialCharacterQuality[id] >= 2).ToArray();
            if (pool.Length > 0) return Create(RewardType.Character, pool[random.Next(pool.Length)], 1, 1);
        }
        RewardGoods? reward = category switch
        {
            "Construct Shard" => DrawCharacterShardReward(draw),
            "4★ Equipment" => DrawMemoryReward(),
            "Overclock Material" => DrawOverclockMaterialReward(),
            "EXP Material" => DrawExpMaterialReward(),
            "Cog Box" => DrawCogBoxReward(),
            "CUB EXP Material" => DrawItemReward(x => x.Name.StartsWith("Integrated CUB EXP"), 1),
            "CUB Overclock Material" => DrawItemReward(x => x.Name.StartsWith("Support Overclock Bundle"), 1),
            "Support Skill Component" => DrawItemReward(x => x.Name == category, 1),
            "CUB Shard" => DrawItemReward(x => Partners.Any(p => PreviewIds(draw).Contains(p.Id) && p.ChipItemId == x.Id), 1),
            _ => null
        };
        return reward ?? throw new InvalidDataException($"Draw {draw.Id} has no reward for {category}");
    }
}
