using System.Globalization;
using AscNet.GameServer.Handlers;

namespace AscNet.GameServer.Commands;

internal abstract class GuideCompletionCommand(Session session, string[] args, bool validate)
    : Command(session, args, validate)
{
    internal const string DryRunFlag = "--dry-run";
    private const string IgnoreClientTriggersFlag = "--ignore-client-triggers";

    [Argument(1, @"\A(?:preserve|grant|skip)\z", "Reward policy")]
    protected string RewardPolicy { get; set; } = string.Empty;

    [Argument(2, @"\A(?:--dry-run|--ignore-client-triggers)\z", "Preview or waive client-only display triggers", ArgumentFlags.Optional)]
    protected string Option { get; set; } = string.Empty;

    [Argument(3, @"\A(?:--dry-run|--ignore-client-triggers)\z", "Second option when using both flags", ArgumentFlags.Optional)]
    protected string SecondOption { get; set; } = string.Empty;

    internal static bool IsAuthorized(Session session)
    {
        long uid = session.player?.PlayerData.Id ?? 0;
        return uid > 0 && (Common.Common.config.GuideGmPlayerIds ?? string.Empty)
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Any(value => long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long allowed)
                && allowed == uid);
    }

    protected static int ParseId(string value)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int id) || id <= 0)
            throw new CommandMessageCallbackException("Use a positive guide or group id from the guide table.");
        return id;
    }

    protected void ExecuteCompletion(int? guideId, int? groupId)
    {
        if (!IsAuthorized(session))
            throw new CommandMessageCallbackException("Guide GM commands are disabled for this account.");
        if (args.Length > 4 || (args.Length == 4 && Option == SecondOption))
            throw new CommandMessageCallbackException("Use each guide option at most once. See /help guide or /help guides.");

        var policy = Enum.Parse<GuideRewardPolicy>(RewardPolicy, ignoreCase: true);
        bool dryRun = Option == DryRunFlag || SecondOption == DryRunFlag;
        bool ignoreClientTriggers = Option == IgnoreClientTriggersFlag || SecondOption == IgnoreClientTriggersFlag;
        throw new CommandMessageCallbackException(
            GmPlayerService.CompleteGuides(session, guideId, groupId, policy, dryRun, ignoreClientTriggers));
    }
}

[CommandName(Name)]
internal sealed class GuideCommand(Session session, string[] args, bool validate = true)
    : GuideCompletionCommand(session, args, validate)
{
    internal const string Name = "guide";
    public override string Help => "Complete an eligible guide for the current account.";

    [Argument(0, @"\A[0-9]+\z", "GuideGroup.Id")]
    private string GuideId { get; set; } = string.Empty;

    public override void Execute() => ExecuteCompletion(ParseId(GuideId), null);
}

[CommandName(Name)]
internal sealed class GuidesCommand(Session session, string[] args, bool validate = true)
    : GuideCompletionCommand(session, args, validate)
{
    internal const string Name = "guides";
    public override string Help => "Complete eligible guides in a group or all eligible common guides for the current account.";

    [Argument(0, @"\A(?:[0-9]+|all)\z", "GuideGroup.GroupId or 'all'")]
    private string GroupOrAll { get; set; } = string.Empty;

    public override void Execute() => ExecuteCompletion(null, GroupOrAll == "all" ? null : ParseId(GroupOrAll));
}
