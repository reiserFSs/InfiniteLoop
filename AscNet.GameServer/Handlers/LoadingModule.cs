using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.config;
using MessagePack;
using System.Globalization;

namespace AscNet.GameServer.Handlers;

internal static class LoadingModule
{
    // EN CodeText: ParamsError and ServerInternalError; no loading-specific errors exist.
    private const int ParamsError = 5;
    private const int ServerInternalError = 2;
    private static readonly Lazy<int> MaxSize = new(() => int.Parse(
        TableReaderV2.Parse<ConfigTable>().Single(row => row.Key == "CustomLoadingMaxSize").Value,
        CultureInfo.InvariantCulture));

    internal static NotifySettingLoadingOption BuildLoginData(Player player) => new()
    {
        LoadingData = new LoadingOptionData
        {
            LoadingType = player.LoadingOption?.LoadingType is 2 ? 2 : 1,
            CgIds = player.LoadingOption?.CgIds ?? new()
        }
    };

    [RequestPacketHandler("SettingLoadingOptionRequest")]
    public static void SettingLoadingOption(Session session, Packet.Request packet)
    {
        LoadingOptionData? data;
        try
        {
            data = packet.Deserialize<SettingLoadingOptionRequest>()?.LoadingData;
        }
        catch (MessagePackSerializationException)
        {
            session.SendResponse(new SettingLoadingOptionResponse { Code = ParamsError }, packet.Id);
            return;
        }

        if (data is null || data.LoadingType is not (1 or 2))
        {
            session.SendResponse(new SettingLoadingOptionResponse { Code = ParamsError }, packet.Id);
            return;
        }

        // EN XMessagePack encodes an empty Lua table as nil, including a cleared CgIds list.
        data.CgIds ??= new();
        if (data.CgIds.Count > MaxSize.Value)
        {
            session.SendResponse(new SettingLoadingOptionResponse { Code = ParamsError }, packet.Id);
            return;
        }

        var seen = new HashSet<int>();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (int id in data.CgIds)
        {
            int code = seen.Add(id) ? ArchiveCgModule.GetSelectionError(session, id, now) : ParamsError;
            if (code != 0)
            {
                session.SendResponse(new SettingLoadingOptionResponse { Code = code }, packet.Id);
                return;
            }
        }

        LoadingOptionData? previous = session.player.LoadingOption;
        var response = new SettingLoadingOptionResponse();
        if (previous is null || previous.LoadingType != data.LoadingType || previous.CgIds is null
            || !previous.CgIds.SequenceEqual(data.CgIds))
        {
            // Mode 1 retains the submitted ordered rotation for the next custom-mode toggle.
            session.player.LoadingOption = data;
            try
            {
                session.player.SaveChecked();
            }
            catch
            {
                session.player.LoadingOption = previous!;
                response.Code = ServerInternalError;
            }
        }
        session.SendResponse(response, packet.Id);
    }
}
