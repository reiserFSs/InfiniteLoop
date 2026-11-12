using System.Text.Json;

namespace AscNet.GameServer.Handlers;

internal sealed record SoloGuidanceMilestone(int Level, int Count, int RewardId);

internal sealed record SoloGuidanceRules(
    int StudentLevel, int InstructorLevel, int GraduationLevel, int AutoGraduationLevel,
    int DailyOfferLimit, int DailyAcceptLimit, int WeeklyAcceptLimit,
    int GraduationRewardId, int ActivationRewardId, int ActivationRewardCount,
    SoloGuidanceMilestone[] WeeklyRewards);

internal static partial class MentorModule
{
    private static readonly Lazy<SoloGuidanceRules?> LocalRules = new(LoadLocalRules);
    internal static Func<SoloGuidanceRules?> ReadRules = () => LocalRules.Value;

    private static SoloGuidanceRules Rules => ReadRules()
        ?? throw new InvalidOperationException("Solo guidance is not configured.");

    private static SoloGuidanceRules? LoadLocalRules()
    {
        const string path = "Configs/solo_guidance.json";
        if (!File.Exists(path))
            return null;

        using FileStream stream = File.OpenRead(path);
        var rules = JsonSerializer.Deserialize<SoloGuidanceRules>(stream);
        if (rules is null || rules.StudentLevel <= 0 || rules.InstructorLevel <= rules.StudentLevel
            || rules.GraduationLevel < rules.StudentLevel || rules.AutoGraduationLevel < rules.GraduationLevel
            || rules.DailyAcceptLimit <= 0 || rules.DailyOfferLimit < rules.DailyAcceptLimit
            || rules.WeeklyAcceptLimit < rules.DailyAcceptLimit || rules.ActivationRewardCount <= 0
            || rules.WeeklyRewards is not { Length: > 0 }
            || rules.WeeklyRewards.Any(row => row.Level < 0 || row.Count <= 0 || row.Count > rules.WeeklyAcceptLimit)
            || rules.WeeklyRewards.Min(row => row.Level) > rules.StudentLevel
            || rules.WeeklyRewards.GroupBy(row => (row.Level, row.Count)).Any(group => group.Count() > 1)
            || rules.WeeklyRewards.Select(row => row.RewardId).Append(rules.ActivationRewardId)
                .Append(rules.GraduationRewardId).Distinct().Any(id => id <= 0 || RewardHandler.GetRewardGoods(id).Count == 0))
            throw new InvalidDataException($"Invalid solo guidance rules in {path}.");
        return rules;
    }
}
