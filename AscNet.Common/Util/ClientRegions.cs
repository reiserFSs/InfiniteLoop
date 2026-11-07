using System.Collections.Concurrent;

namespace AscNet.Common.Util;

/// <summary>
/// Which regional client a login belongs to. The shared tables are EN-derived, but a TW/KR/JP/CN client resolves ids against
/// its own tables, so the server must know the region to send only ids that client has (<see cref="TableReaderV2.Parse{T}(string?)"/>).
/// The TCP login carries no package: the gate request is the only link between the SDK package and the player. Each regional
/// package's <c>ServerListStr</c> points at <c>/api/Login/Login-&lt;region&gt;</c> (EN: plain <c>/api/Login/Login</c>); the gate
/// remembers the region by the player's game token, and the TCP LoginRequest persists it on the player (a reconnect after a
/// server restart has no gate request).
/// </summary>
public static class ClientRegions
{
    public const string Default = "en";
    public static readonly string[] GateRegions = ["tw", "kr", "jp", "cn"];
    private static readonly ConcurrentDictionary<string, string> byPlayerToken = new();

    public static void Remember(string playerToken, string region) => byPlayerToken[playerToken] = region;

    public static string? Recall(string playerToken) => byPlayerToken.GetValueOrDefault(playerToken);

    /// <summary>The region of an SDK package id; null for EN (and unknown packages, which keep the shared EN tables).</summary>
    public static string? FromPackage(string? package) => package switch
    {
        "com.kurogame.punishing.grayraven.tw" => "tw",
        "com.kurogame.punishing.grayraven.kr" => "kr",
        "com.kurogame.punishing.grayraven.jp" => "jp",
        "com.kurogame.haru.kuro" or "com.kurogame.haru.hero" => "cn",
        _ => null
    };

    /// <summary>The gate path's region: <c>/api/Login/Login-tw</c> is "tw", plain <c>/api/Login/Login</c> is EN.</summary>
    public static string FromGatePath(string? path) =>
        path is not null && path.StartsWith(GatePrefix, StringComparison.Ordinal) && GateRegions.Contains(path[GatePrefix.Length..])
            ? path[GatePrefix.Length..] : Default;

    private const string GatePrefix = "/api/Login/Login-";
}
