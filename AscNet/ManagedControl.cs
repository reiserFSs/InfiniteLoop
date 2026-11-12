using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using AscNet.GameServer.Commands;
using AscNet.Logging;

namespace AscNet;

internal static class ManagedControl
{
    private const string Prefix = "ASCNET-GM/1 ";
    private const int MaxLineLength = 4096;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    internal static void Start()
    {
        if (Environment.GetEnvironmentVariable("ASCNET_MANAGED_STDIN") != "1")
            return;

        // The launcher owns these redirected handles. The game and SDK sockets never expose this channel.
        bool gmEnabled = Environment.GetEnvironmentVariable("ASCNET_MANAGED_GM") == "1"
            && Console.IsInputRedirected && Console.IsOutputRedirected
            && Environment.GetEnvironmentVariable("ASCNET_GAME_BIND_ADDRESS") == "127.0.0.1"
            && Uri.TryCreate(Environment.GetEnvironmentVariable("ASCNET_PUBLIC_HTTP_ORIGIN"), UriKind.Absolute, out Uri? origin)
            && origin.Scheme == Uri.UriSchemeHttp && origin.Host == "127.0.0.1";

        new Thread(() => ReadCommands(gmEnabled)) { IsBackground = true, Name = "Launcher control" }.Start();
    }

    private static void ReadCommands(bool gmEnabled)
    {
        try
        {
            while (Console.ReadLine() is { } line)
            {
                if (line.Trim().Equals("shutdown", StringComparison.OrdinalIgnoreCase))
                    Environment.Exit(0);

                if (gmEnabled && line.Length <= MaxLineLength && line.StartsWith(Prefix, StringComparison.Ordinal)
                    && ProcessRequest(line[Prefix.Length..]) is { } reply)
                    Console.WriteLine(Prefix + JsonSerializer.Serialize(reply, Json));
            }
        }
        catch (IOException)
        {
            // The launcher closed its pipe.
        }
    }

    private static Reply? ProcessRequest(string json)
    {
        string? id = null;
        try
        {
            using JsonDocument frame = JsonDocument.Parse(json);
            if (frame.RootElement.ValueKind != JsonValueKind.Object
                || !frame.RootElement.TryGetProperty("id", out JsonElement correlation)
                || correlation.ValueKind != JsonValueKind.String
                || !Guid.TryParseExact(correlation.GetString(), "N", out _))
                return null;

            id = correlation.GetString()!;
            Request request = frame.RootElement.Deserialize<Request>(Json)!;
            long uid = request.Uid ?? 0;
            switch (request.Operation)
            {
                case "hello":
                    string version = typeof(ManagedControl).Assembly
                        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
                    return new(id, true, ServerVersion: version);
                case "readLevel":
                    return new(id, true, Player: GmPlayerService.ReadLevel(uid));
                case "setLevel" when request.Level is int level:
                    GmPlayerLevel result = GmPlayerService.SetLevel(uid, level);
                    LoggerFactory.Info($"Launcher GM confirmed level uid={result.Uid} level={result.Level} online={result.Online} relog={result.RequiresRelog}");
                    return new(id, true, Player: result);
                case "guides":
                    string feedback = GmPlayerService.CompleteGuides(uid, request.GuideId, request.GroupId,
                        request.All, request.RewardPolicy, request.DryRun, request.IgnoreClientTriggers);
                    return new(id, true, Message: feedback);
                default:
                    return new(id, false, "invalid_request", "Unknown operation or required argument missing.");
            }
        }
        catch (JsonException)
        {
            return id is null ? null : new(id, false, "invalid_request", "Invalid launcher control request.");
        }
        catch (GmOperationException exception)
        {
            if (exception.InnerException is { } inner)
                LoggerFactory.Error($"Launcher GM {exception.Code} ({id}): {inner}");
            return new(id!, false, exception.Code, exception.Message);
        }
        catch (Exception exception)
        {
            LoggerFactory.Error($"Launcher GM failed ({id}): {exception}");
            return id is null ? null : new(id, false, "operation_failed", "The server could not finish the operation. Check the server log and read the current state before retrying.");
        }
    }

    private sealed class Request
    {
        public string? Id { get; set; }
        public string? Operation { get; set; }
        public long? Uid { get; set; }
        public int? Level { get; set; }
        public int? GuideId { get; set; }
        public int? GroupId { get; set; }
        public bool All { get; set; }
        public string? RewardPolicy { get; set; }
        public bool DryRun { get; set; }
        public bool IgnoreClientTriggers { get; set; }
    }

    private sealed record Reply(string Id, bool Ok, string? Code = null, string? Message = null,
        GmPlayerLevel? Player = null, string? ServerVersion = null);
}
