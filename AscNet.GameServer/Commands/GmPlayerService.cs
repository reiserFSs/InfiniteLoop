using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.GameServer.Handlers;
using MongoDB.Driver;

namespace AscNet.GameServer.Commands;

public sealed class GmOperationException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
}

public sealed record GmPlayerLevel(long Uid, long Level, bool Online, bool RequiresRelog);

/// <summary>Progression operations for the authenticated chat session and the host's private launcher channel.</summary>
public static class GmPlayerService
{
    public static GmPlayerLevel ReadLevel(long uid) =>
        WithPlayer(uid, LevelState);

    public static GmPlayerLevel SetLevel(long uid, int level)
    {
        ValidateLevel(level);
        return WithPlayer(uid, (player, session) => SaveLevel(player, session, level));
    }

    internal static GmPlayerLevel SetLevel(Session session, int level)
    {
        ValidateLevel(level);
        return WithSession(session, () => SaveLevel(session.player, session, level));
    }

    public static string CompleteGuides(long uid, int? guideId, int? groupId, bool all, string? rewardPolicy,
        bool dryRun, bool ignoreClientTriggers)
    {
        if ((guideId.HasValue ? 1 : 0) + (groupId.HasValue ? 1 : 0) + (all ? 1 : 0) != 1
            || guideId is <= 0 || groupId is <= 0)
            throw new GmOperationException("invalid_guide_selection", "Select a guide id, a group id, or all eligible guides.");

        GuideRewardPolicy policy = rewardPolicy switch
        {
            "preserve" => GuideRewardPolicy.Preserve,
            "grant" => GuideRewardPolicy.Grant,
            "skip" => GuideRewardPolicy.Skip,
            _ => throw new GmOperationException("invalid_reward_policy", "Select preserve, grant, or skip.")
        };

        return WithPlayer(uid, (_, session) =>
        {
            if (session is null || session.character is null || session.inventory is null || session.stage is null)
                throw new GmOperationException("player_offline", $"Sign in as player {uid} before using guide tools.");
            return GuideModule.ExecuteGmCompletion(session, guideId, groupId, policy, dryRun, ignoreClientTriggers);
        });
    }

    internal static string CompleteGuides(Session session, int? guideId, int? groupId, GuideRewardPolicy policy,
        bool dryRun, bool ignoreClientTriggers) =>
        WithSession(session, () => GuideModule.ExecuteGmCompletion(session, guideId, groupId, policy, dryRun, ignoreClientTriggers));

    private static T WithPlayer<T>(long uid, Func<Player, Session?, T> operation) =>
        WithOnlinePlayer(uid, session =>
        {
            if (session is not null)
                return operation(session.player, session);
            Player? player = Player.collection.Find(row => row.PlayerData.Id == uid).SingleOrDefault();
            if (player is null)
                throw new GmOperationException("player_not_found", $"Player {uid} does not have a saved character.");
            return operation(player, null);
        });

    internal static T WithOnlinePlayer<T>(long uid, Func<Session?, T> operation)
    {
        if (uid <= 0)
            throw new GmOperationException("invalid_uid", "Enter a positive player UID.");

        // Packet dispatch, login and disconnect acquire these locks in this order.
        lock (GuildModule.MembershipLock)
        lock (Session.GetPlayerOperationLock(uid))
            return operation(Server.Instance.SessionFromUID(uid));
    }

    private static T WithSession<T>(Session session, Func<T> operation)
    {
        lock (GuildModule.MembershipLock)
        lock (Session.GetPlayerOperationLock(session.player.PlayerData.Id))
        {
            if (session.IsDisconnected)
                throw new GmOperationException("player_offline", "The player session has ended.");
            return operation();
        }
    }

    private static void ValidateLevel(int level)
    {
        if (!SessionExtensions.IsConfiguredPlayerLevel(level))
            throw new GmOperationException("invalid_level", $"Level {level} is unavailable in the server's Player table.");
    }

    private static GmPlayerLevel LevelState(Player player, Session? session) =>
        new(player.PlayerData.Id, player.PlayerData.Level, session is not null, player.GmLevelSync == GmLevelSyncMode.Relog);

    private static GmPlayerLevel SaveLevel(Player player, Session? session, int level)
    {
        long previous = player.PlayerData.Level;
        if (level == previous)
            return LevelState(player, session);

        GmLevelSyncMode? previousSync = player.GmLevelSync;
        GmLevelSyncMode nextSync = level < previous || previousSync == GmLevelSyncMode.Relog
            ? GmLevelSyncMode.Relog : GmLevelSyncMode.Notify;
        player.PlayerData.Level = level;
        player.GmLevelSync = nextSync;
        try
        {
            player.SaveChecked();
        }
        catch (Exception exception)
        {
            player.PlayerData.Level = previous;
            player.GmLevelSync = previousSync;
            Player? durable = ReadAfterFailedSave(player, session, exception);
            if (durable is null || durable.PlayerData.Level != level || durable.GmLevelSync != nextSync)
            {
                if (durable is null || durable.PlayerData.Level != previous || durable.GmLevelSync != previousSync)
                    session?.DisconnectProtocol(persistState: false);
                throw new GmOperationException("save_failed", "The level save could not be confirmed. Read the current level before retrying.", exception);
            }
            player.PlayerData.Level = level;
            player.GmLevelSync = nextSync;
        }

        if (session is not null)
        {
            if (nextSync == GmLevelSyncMode.Notify)
                session.SendPush(new NotifyPlayerLevel { Level = level });
            else
                _ = Task.Run(() => LogoutForLevelChange(session));
        }
        return LevelState(player, session);
    }

    private static Player? ReadAfterFailedSave(Player player, Session? session, Exception writeFailure)
    {
        try
        {
            return Player.collection.Find(row => row.Id == player.Id).SingleOrDefault();
        }
        catch (Exception readFailure)
        {
            // A later disconnect must not replace a potentially committed level with stale state.
            session?.DisconnectProtocol(persistState: false);
            throw new GmOperationException("save_unconfirmed", "The database could not confirm the level. Reconnect and read the current level before retrying.",
                new AggregateException(writeFailure, readFailure));
        }
    }

    private static void LogoutForLevelChange(Session session)
    {
        lock (GuildModule.MembershipLock)
        lock (Session.GetPlayerOperationLock(session.player.PlayerData.Id))
        {
            if (session.IsDisconnected || session.player.GmLevelSync != GmLevelSyncMode.Relog)
                return;
            // The client only accepts increasing NotifyPlayerLevel values.
            session.SendPush(new ForceLogoutNotify { Code = 1030 });
            session.DisconnectProtocol();
        }
    }
}
