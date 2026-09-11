using System.Globalization;
using AscNet.Common.Util;
using AscNet.Table.V2.client.loading;
using AscNet.Table.V2.share.archive;
using MessagePack;

namespace AscNet.GameServer.Handlers;

[MessagePackObject(true)]
public sealed class NotifyArchiveCgs
{
    public List<uint> UnlockCgs { get; set; } = new();
}

public static partial class ArchiveCgModule
{
    private sealed record Cg(int Id, int GroupId, int Condition, DateTimeOffset? UnlockTime);

    private static readonly Lazy<Dictionary<int, Cg>> Catalog = new(() =>
        TableReaderV2.Parse<CGDetailTable>().ToDictionary(row => row.Id, row => new Cg(
            row.Id, row.GroupId, row.Condition ?? 0,
            string.IsNullOrWhiteSpace(row.UnLockTime) || row.UnLockTime == "0" ? null :
                DateTimeOffset.Parse(row.UnLockTime, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal))));
    private static readonly Lazy<HashSet<int>> BlockedGroups = new(() =>
        TableReaderV2.Parse<CustomLoadingTable>().SelectMany(row => row.BlockGroup).ToHashSet());

    private static bool Earned(Session session, Cg cg, DateTimeOffset now) =>
        (cg.Condition == 0 && cg.UnlockTime is null)
        || (cg.UnlockTime is { } unlockTime && now >= unlockTime)
        || (cg.Condition > 0 && ConditionSatisfied(session, cg.Condition, now));

    public static HashSet<int> GetUnlockedCgs(Session session, DateTimeOffset now)
    {
        HashSet<int> unlocked = new(session.player.ArchiveUnlockedCgs);
        foreach (Cg cg in Catalog.Value.Values)
            if (!unlocked.Contains(cg.Id) && Earned(session, cg, now))
                unlocked.Add(cg.Id);
        return unlocked;
    }

    public static int GetSelectionError(Session session, int cgId, DateTimeOffset now)
    {
        if (!Catalog.Value.TryGetValue(cgId, out Cg? cg) || BlockedGroups.Value.Contains(cg.GroupId))
            return 20065003; // ArchiveInvalidId
        return session.player.ArchiveUnlockedCgs.Contains(cgId) || Earned(session, cg, now)
            ? 0 : 20065004; // ArchiveConditionNotFit
    }

    public static void Reconcile(Session session, bool notify)
    {
        if (session.player is null || session.stage is null || session.character is null || session.inventory is null)
            return;

        lock (Session.GetPlayerOperationLock(session.player.PlayerData.Id))
        {
            HashSet<int> previous = session.player.ArchiveUnlockedCgs;
            HashSet<int>? unlocked = null;
            List<uint>? added = null;
            DateTimeOffset now = DateTimeOffset.UtcNow;
            foreach (Cg cg in Catalog.Value.Values)
            {
                if (previous.Contains(cg.Id) || !Earned(session, cg, now))
                    continue;
                (unlocked ??= new(previous)).Add(cg.Id);
                (added ??= new()).Add(checked((uint)cg.Id));
            }
            if (unlocked is null)
                return;

            session.player.ArchiveUnlockedCgs = unlocked;
            try
            {
                session.player.SaveChecked();
            }
            catch
            {
                session.player.ArchiveUnlockedCgs = previous;
                throw;
            }
            if (notify)
                session.SendPush(new NotifyArchiveCgs { UnlockCgs = added! });
        }
    }
}
